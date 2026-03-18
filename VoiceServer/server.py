# =========================
# LLM-DRIVEN XR SERVER
# No keywords — pure natural language → LLM → Unity commands
# Includes: Meshy.ai 3D generation, OpenAI image/texture generation
# =========================

from fastapi import FastAPI, File, Form, UploadFile, BackgroundTasks
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
from typing import Optional, Dict, Any, List
from PIL import Image, ImageDraw
import os, json, re, time, base64, io
from datetime import datetime
from faster_whisper import WhisperModel
from openai import OpenAI
from dotenv import load_dotenv
import requests

load_dotenv()

app = FastAPI()

# =========================
# CONFIG
# =========================
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
client = OpenAI(api_key=OPENAI_API_KEY)
print("OPENAI_API_KEY loaded:", bool(OPENAI_API_KEY))

# ⭐ MODEL CHOICE:
# "o4-mini"   → Best for this task: fast, cheap, excellent structured JSON output  ← RECOMMENDED
# "gpt-4o"    → Also great, very fast, slightly weaker at strict JSON formatting
# "o3"        → Most powerful reasoning, but slower and more expensive
LLM_MODEL = "o4-mini"

BASE_PATH = os.path.join(
    os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "VRTApp-TestLocal"
)
os.makedirs(BASE_PATH, exist_ok=True)

TRANSCRIPT_FILE = os.path.join(BASE_PATH, "unity_transcripts.txt")
TEMP_AUDIO_PATH = os.path.join(BASE_PATH, "temp.wav")

MODEL_DIR = os.path.join(BASE_PATH, "GeneratedModels")
os.makedirs(MODEL_DIR, exist_ok=True)

POSTER_DIR = os.path.join(BASE_PATH, "posters")
os.makedirs(POSTER_DIR, exist_ok=True)

TEXTURE_DIR = os.path.join(BASE_PATH, "textures")
os.makedirs(TEXTURE_DIR, exist_ok=True)

# Serve generated files to Unity over HTTP
app.mount("/files",    StaticFiles(directory=MODEL_DIR),  name="files")
app.mount("/posters",  StaticFiles(directory=POSTER_DIR), name="posters")
app.mount("/textures", StaticFiles(directory=TEXTURE_DIR),name="textures")

# =========================
# MESHY CONFIG
# =========================
MESHY_API_KEY = os.getenv("MESHY_API_KEY")
print("MESHY_API_KEY loaded:", bool(MESHY_API_KEY))

MESHY_BASE             = "https://api.meshy.ai"
MESHY_TEXT2_3D_CREATE  = f"{MESHY_BASE}/openapi/v2/text-to-3d"
MESHY_TEXT2_3D_GET     = f"{MESHY_BASE}/openapi/v2/text-to-3d"

# In-memory job tracker for background Meshy tasks
jobs: Dict[str, Dict[str, Any]] = {}

# =========================
# WHISPER
# =========================
print("Loading Whisper model...")
whisper_model = WhisperModel("small", device="cpu", compute_type="int8")
print("Whisper model ready.")

# =========================
# SYSTEM PROMPT
# =========================
SYSTEM_PROMPT = """
You are an AI agent that controls a Unity XR/VR environment by interpreting natural speech commands.

Your ONLY job is to output a valid JSON object with a "commands" array. No explanations. No markdown. No extra text. Just raw JSON.

=== AVAILABLE ACTIONS ===

1. generate_model
   - Creates a 3D object from a text description using Meshy.ai
   - Required: "prompt" (string), "name" (string, PascalCase object name)
   - Optional: "position" ({"x","y","z"}), "scale" ({"x","y","z"}), "stage" ("preview" or "refine"), "art_style" ("realistic" or "cartoon")
   - Example: {"action":"generate_model","prompt":"wooden dining chair","name":"Chair_01","stage":"preview","art_style":"realistic"}

2. create_poster
   - Generates and displays a flat image/poster on a wall or surface using DALL-E
   - Required: "image_prompt" (string)
   - Optional: "width_m" (float, meters), "height_m" (float, meters), "position" ({"x","y","z"})
   - Example: {"action":"create_poster","image_prompt":"sunset over mountains","width_m":1.5,"height_m":1.0}

3. set_wall_texture
   - Applies a generated seamless texture to a wall or surface using DALL-E
   - Required: "texture_prompt" (string)
   - Optional: "target" (string: "wall", "floor", "ceiling", or object name)
   - Example: {"action":"set_wall_texture","texture_prompt":"rough stone bricks","target":"wall"}

4. translate
   - Moves an object by a relative offset in meters
   - Required: "dx" (float), "dy" (float), "dz" (float)
   - Optional: "target" (string, object name — omit to move last selected object)
   - Example: {"action":"translate","target":"Chair_01","dx":2.0,"dy":0,"dz":0}
   - Note: dz=left/right (left=positive dz, right=negative dz), dy=up/down, dx=forward/back (forward=negative dx, back=positive dx)

5. scale
   - Scales an object uniformly or per axis
   - Required: "factor" (float) OR "sx","sy","sz" (float, per axis)
   - Optional: "target" (string)
   - Example: {"action":"scale","target":"Chair_01","factor":1.5}

6. set_color
   - Changes the material color of an object
   - Required: "color" (string: color name or hex like "#FF0000")
   - Optional: "target" (string)
   - Example: {"action":"set_color","target":"Chair_01","color":"red"}

7. rotate
   - Rotates an object by degrees
   - Required: "rx" (float), "ry" (float), "rz" (float) — degrees around each axis
   - Optional: "target" (string)
   - Example: {"action":"rotate","target":"Chair_01","rx":0,"ry":90,"rz":0}

8. delete_object
   - Removes an object from the scene
   - Required: "target" (string, object name)
   - Example: {"action":"delete_object","target":"Chair_01"}

9. run_code
    - Generates and attaches a C# script to implement ANY behaviour at runtime
    - Use for: animations, physics, particles, custom interactions, effects, or ANYTHING not covered by other actions
    - FALLBACK RULE: if no other action fits the user intent, ALWAYS use run_code — never invent action names
    - Required: "behaviour_prompt" (string — describe in detail what the C# script should do, including values and timing)
    - Optional: "target" (string — omit to apply to last selected object)
    - Example: {"action":"run_code","target":"Ball_01","behaviour_prompt":"make the object bounce up and down with sine wave motion, height 0.5m, period 1 second"}

10. set_lighting
    - Changes scene lighting
    - Required: "preset" (string: "day", "night", "sunset", "studio", "dramatic") OR "color" (hex), "intensity" (0.0–2.0)
    - Example: {"action":"set_lighting","preset":"sunset"}

11. duplicate_object
   - Duplicates/copies an existing object and places the copy at an offset from the original
   - Required: "target" (string, name of the object to copy), "new_name" (string, PascalCase name for the copy)
   - Optional: "dx" (float), "dy" (float), "dz" (float) — offset in meters from the original (default 0,0,0)
   - Use this whenever the user says: "copy", "duplicate", "clone", "make another one", "make a copy of"
   - Offset guide: "on top of" → dy=1.0, "next to" → dx=1.0, "behind" → dz=-1.0, "in front of" → dz=1.0
   - Example: {"action":"duplicate_object","target":"Cube_01","new_name":"Cube_02","dx":0,"dy":1.0,"dz":0}

12. no_action
    - Use when the intent is unclear, the request is conversational, or nothing should happen
    - Example: {"action":"no_action","reason":"unclear intent"}

=== OUTPUT FORMAT ===
{
  "commands": [
    { "action": "...", ... },
    { "action": "...", ... }
  ]
}

=== GAZE TARGET ===
The user is wearing an XR headset with eye tracking. The object they are currently looking at is provided as GAZE_TARGET.
- If the user says "it", "this", "that", "the object", or refers to something without naming it — use GAZE_TARGET as the "target" value.
- If GAZE_TARGET is "none" or empty, the user is not looking at any specific object.
- NEVER guess or invent object names like "Cube_01" or "Chair_01" as target — always use GAZE_TARGET for the currently selected object.
- If the action is global (lighting, create new object, textures) then "target" is not needed.

=== RULES ===
- Output ONLY raw JSON. No markdown. No backticks. No explanations.
- You may return MULTIPLE commands in one response (e.g. generate then color).
- If the user wants a simple shape (cube/sphere), prefer spawn_primitive over generate_model.
- If the user says "it" or "that", they mean the last object mentioned or created.
- When translating directions: "left"→ positive dz, "right"→ negative dz, "up"→ positive dy, "down"→ negative dy, "forward"→ negative dx, "back"→ positive dx. NEVER mix these up — left/right is always dz, forward/back is always dx.
- If intent is ambiguous but partially clear, make a reasonable interpretation.
- Only return no_action if the input is purely conversational (greetings, questions, gibberish) with zero XR intent.

=== FALLBACK RULE (MOST IMPORTANT) ===
If the user's intent is clear but NO existing action covers it, you MUST use run_code as a fallback.
NEVER invent new action names. NEVER return no_action just because the action doesn't exist in the schema.
run_code can do ANYTHING in Unity via generated C# — use it for: physics, animations, particles, custom behaviours, grouping, constraints, lighting effects, or ANY creative/interactive effect.
When using run_code as a fallback, write a detailed "behaviour_prompt" describing exactly what the C# script should do so Unity can implement it correctly.

=== EXAMPLES ===

User: "create a red wooden chair"
{"commands":[{"action":"generate_model","prompt":"wooden chair","name":"Chair_01","stage":"preview","art_style":"realistic"},{"action":"set_color","target":"Chair_01","color":"red"}]}

User: "make a poster of a snowy mountain landscape"
{"commands":[{"action":"create_poster","image_prompt":"snowy mountain landscape","width_m":1.5,"height_m":1.0}]}

User: "apply brick texture to the wall"
{"commands":[{"action":"set_wall_texture","texture_prompt":"old red brick wall","target":"wall"}]}

User: "move it 2 meters to the right and 1 meter up"
GAZE_TARGET: "Cube_03"
{"commands":[{"action":"translate","target":"Cube_03","dx":0,"dy":1.0,"dz":-2.0,"coord_space":"world"}]}

User: "make it twice as big"
GAZE_TARGET: "Chair_02"
{"commands":[{"action":"scale","target":"Chair_02","factor":2.0}]}

User: "turn it blue"
GAZE_TARGET: "Sphere_01"
{"commands":[{"action":"set_color","target":"Sphere_01","color":"blue","coord_space":"world"}]}

User: "make it bounce"
GAZE_TARGET: "Ball_01"
{"commands":[{"action":"run_code","target":"Ball_01","behaviour_prompt":"make the object bounce up and down continuously with a height of 0.5 meters and smooth easing"}]}

User: "make it spin"
GAZE_TARGET: "Cube_01"
{"commands":[{"action":"run_code","target":"Cube_01","behaviour_prompt":"rotate the object around its Y axis continuously at 90 degrees per second"}]}

User: "make a copy of this and put it on top"
GAZE_TARGET: "Cube_01"
{"commands":[{"action":"duplicate_object","target":"Cube_01","new_name":"Cube_02","dx":0,"dy":1.0,"dz":0}]}

User: "delete this"
GAZE_TARGET: "Table_02"
{"commands":[{"action":"delete_object","target":"Table_02"}]}

User: "rotate it 45 degrees to the left"
GAZE_TARGET: "Chair_01"
{"commands":[{"action":"rotate","target":"Chair_01","rx":0,"ry":-45,"rz":0}]}

User: "make it follow me"
GAZE_TARGET: "Lamp_01"
{"commands":[{"action":"run_code","target":"Lamp_01","behaviour_prompt":"make the object smoothly follow the XR player/camera position, maintaining a distance of 1.5 meters in front and 0.5 meters below eye level, using Lerp for smooth movement"}]}

User: "hello how are you"
{"commands":[{"action":"no_action","reason":"conversational input, no XR action needed"}]}
"""

# =========================
# MESHY HELPERS
# =========================

def _safe_name(name: str) -> str:
    safe = re.sub(r"[^a-zA-Z0-9_\-]", "_", (name or "").strip())
    return safe if safe else "generated_model"


def _meshy_headers() -> Dict[str, str]:
    return {
        "Authorization": f"Bearer {MESHY_API_KEY}",
        "Accept": "application/json",
        "Content-Type": "application/json",
    }


def _meshy_create_task(mode: str, payload: Dict[str, Any]) -> str:
    body = {"mode": mode, **payload}
    resp = requests.post(MESHY_TEXT2_3D_CREATE, headers=_meshy_headers(), json=body, timeout=60)
    if not resp.ok:
        print("[meshy] CREATE FAILED:", resp.status_code, resp.text, flush=True)
        resp.raise_for_status()
    data = resp.json()
    task_id = data.get("result")
    if not task_id:
        raise RuntimeError(f"Meshy create task response missing 'result': {data}")
    return task_id


def _meshy_get_task(task_id: str) -> Dict[str, Any]:
    url = f"{MESHY_TEXT2_3D_GET}/{task_id}"
    resp = requests.get(url, headers=_meshy_headers(), timeout=60)
    if not resp.ok:
        print("[meshy] GET FAILED:", resp.status_code, resp.text, flush=True)
        resp.raise_for_status()
    return resp.json()


def _download_to_file(url: str, out_path: str):
    r = requests.get(url, timeout=180)
    if not r.ok:
        print("[meshy] DOWNLOAD FAILED:", r.status_code, r.text[:200], flush=True)
        r.raise_for_status()
    with open(out_path, "wb") as f:
        f.write(r.content)


def _set_job_progress(safe: str, stage: str, task_id: str, meshy_status: str, progress: Any):
    try:
        p = int(progress) if progress is not None else 0
    except Exception:
        p = 0
    jobs[safe] = {
        "stage": stage,
        "task_id": task_id,
        "meshy_status": str(meshy_status or "PENDING"),
        "progress": p,
        "status": "RUNNING",
        "error": jobs.get(safe, {}).get("error", ""),
    }


def _generate_with_meshy_background(prompt: str, name: str, stage: str, art_style: str):
    safe = _safe_name(name)
    out_glb = os.path.join(MODEL_DIR, f"{safe}.glb")

    try:
        if not MESHY_API_KEY:
            raise RuntimeError("MESHY_API_KEY not set.")

        jobs[safe] = {
            "status": "RUNNING", "stage": stage, "task_id": None,
            "meshy_status": "PENDING", "progress": 0, "error": "",
        }

        # --- Preview stage ---
        preview_task_id = _meshy_create_task(
            mode="preview",
            payload={"prompt": prompt, "art_style": art_style, "should_remesh": True},
        )
        _set_job_progress(safe, stage, preview_task_id, "PENDING", 0)

        preview_task = None
        deadline = time.time() + 20 * 60
        while time.time() < deadline:
            t = _meshy_get_task(preview_task_id)
            meshy_status = t.get("status") or "PENDING"
            progress = t.get("progress") or 0
            _set_job_progress(safe, stage, preview_task_id, meshy_status, progress)
            print(f"[meshy] preview {preview_task_id} status={meshy_status} progress={progress}", flush=True)
            if meshy_status == "SUCCEEDED" and t.get("model_urls", {}).get("glb"):
                preview_task = t
                break
            if meshy_status in ("FAILED", "CANCELED"):
                raise RuntimeError(f"Meshy preview failed: {t.get('task_error') or t}")
            time.sleep(3)

        if preview_task is None:
            raise RuntimeError("Timed out waiting for Meshy preview task.")

        # If only preview requested, download and done
        if (stage or "preview").lower() == "preview":
            glb_url = preview_task["model_urls"]["glb"]
            _download_to_file(glb_url, out_glb)
            jobs[safe]["status"] = "DONE"
            jobs[safe]["meshy_status"] = "SUCCEEDED"
            jobs[safe]["progress"] = 100
            print(f"[meshy] preview done → {out_glb}", flush=True)
            return

        # --- Refine stage ---
        refine_task_id = _meshy_create_task(
            mode="refine",
            payload={"preview_task_id": preview_task_id, "enable_pbr": True},
        )
        _set_job_progress(safe, stage, refine_task_id, "PENDING", 0)

        refine_task = None
        deadline = time.time() + 30 * 60
        while time.time() < deadline:
            t = _meshy_get_task(refine_task_id)
            meshy_status = t.get("status") or "PENDING"
            progress = t.get("progress") or 0
            _set_job_progress(safe, stage, refine_task_id, meshy_status, progress)
            print(f"[meshy] refine {refine_task_id} status={meshy_status} progress={progress}", flush=True)
            if meshy_status == "SUCCEEDED" and t.get("model_urls", {}).get("glb"):
                refine_task = t
                break
            if meshy_status in ("FAILED", "CANCELED"):
                raise RuntimeError(f"Meshy refine failed: {t.get('task_error') or t}")
            time.sleep(3)

        if refine_task is None:
            raise RuntimeError("Timed out waiting for Meshy refine task.")

        glb_url = refine_task["model_urls"]["glb"]
        _download_to_file(glb_url, out_glb)
        jobs[safe]["status"] = "DONE"
        jobs[safe]["meshy_status"] = "SUCCEEDED"
        jobs[safe]["progress"] = 100
        print(f"[meshy] refine done → {out_glb}", flush=True)

    except Exception as e:
        print("[meshy] ERROR:", e, flush=True)
        jobs[safe] = {
            "status": "ERROR", "stage": stage,
            "task_id": jobs.get(safe, {}).get("task_id"),
            "meshy_status": "FAILED", "progress": 0, "error": str(e),
        }


# =========================
# IMAGE GENERATION HELPERS
# =========================

def _make_ai_poster(prompt: str, out_path: str, w: int, h: int):
    result = client.images.generate(
        model="gpt-image-1",
        prompt=f"Poster artwork, high quality, no text unless requested. {prompt}",
        size="1024x1024",
    )
    img_bytes = base64.b64decode(result.data[0].b64_json)
    img = Image.open(io.BytesIO(img_bytes)).convert("RGB")
    if (w, h) != (1024, 1024):
        img = img.resize((w, h), Image.LANCZOS)
    img.save(out_path, "PNG")


def _make_ai_texture(prompt: str, out_path: str, size_px: int = 1024):
    result = client.images.generate(
        model="gpt-image-1",
        prompt=f"Seamless tileable texture. Realistic. No perspective. Even lighting. No text. {prompt}",
        size=f"{size_px}x{size_px}",
    )
    img_bytes = base64.b64decode(result.data[0].b64_json)
    img = Image.open(io.BytesIO(img_bytes)).convert("RGB")
    img.save(out_path, "PNG")


def _make_placeholder_poster(prompt: str, out_path: str, w: int, h: int):
    """Fallback placeholder if image generation fails."""
    img = Image.new("RGB", (w, h), (245, 245, 245))
    draw = ImageDraw.Draw(img)
    draw.rectangle([8, 8, w - 8, h - 8], outline=(40, 40, 40), width=4)
    text = (prompt or "Poster").strip()[:220]
    max_chars = 38 if w >= h else 30
    lines = [text[i:i + max_chars] for i in range(0, len(text), max_chars)]
    y = 40
    for ln in lines[:12]:
        draw.text((30, y), ln, fill=(20, 20, 20))
        y += 28
    img.save(out_path, "PNG")


# =========================
# TEXT-TO-3D ENDPOINT
# =========================

class TextTo3DRequest(BaseModel):
    prompt: str
    name: str
    stage: Optional[str] = "preview"
    art_style: Optional[str] = "realistic"


@app.post("/api/text-to-3d")
def text_to_3d(req: TextTo3DRequest, background_tasks: BackgroundTasks):
    safe = _safe_name(req.name)
    glb_filename = f"{safe}.glb"
    glb_path = os.path.join(MODEL_DIR, glb_filename)

    if not MESHY_API_KEY:
        return JSONResponse(
            status_code=500,
            content={"status": "FAILED", "progress": 0, "message": "MESHY_API_KEY missing."},
        )

    # Already generated — return immediately
    if os.path.exists(glb_path):
        return JSONResponse(status_code=200, content={
            "status": "SUCCEEDED", "progress": 100,
            "downloadUrl": f"http://localhost:8000/files/{glb_filename}",
        })

    job = jobs.get(safe)

    # Job still running
    if job and job.get("status") == "RUNNING":
        return JSONResponse(status_code=202, content={
            "status": job.get("meshy_status", "IN_PROGRESS"),
            "progress": int(job.get("progress", 0) or 0),
        })

    # Previous job errored
    if job and job.get("status") == "ERROR":
        return JSONResponse(status_code=500, content={
            "status": "FAILED", "progress": 0,
            "message": job.get("error", "Unknown error"),
        })

    # Start new background job
    stage = (req.stage or "preview").lower()
    art_style = (req.art_style or "realistic").lower()
    print(f"[text-to-3d] starting name={req.name} safe={safe} stage={stage} art_style={art_style}", flush=True)

    jobs[safe] = {
        "status": "RUNNING", "stage": stage, "task_id": None,
        "meshy_status": "PENDING", "progress": 0, "error": "",
    }

    background_tasks.add_task(_generate_with_meshy_background, req.prompt, req.name, stage, art_style)
    return JSONResponse(status_code=202, content={"status": "PENDING", "progress": 0})


# =========================
# POSTER IMAGE ENDPOINT
# =========================

class PosterImageRequest(BaseModel):
    prompt: str
    width_px: int = 1024
    height_px: int = 1024


@app.post("/api/poster-image")
def poster_image(req: PosterImageRequest):
    prompt = (req.prompt or "").strip() or "A minimal poster"
    w = max(256, min(2048, int(req.width_px or 1024)))
    h = max(256, min(2048, int(req.height_px or 1024)))

    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    filename = f"poster_{ts}.png"
    out_path = os.path.join(POSTER_DIR, filename)

    try:
        _make_ai_poster(prompt, out_path, w, h)
    except Exception as e:
        print(f"[poster] AI generation failed: {e}, using placeholder", flush=True)
        _make_placeholder_poster(prompt, out_path, w, h)

    return JSONResponse(content={"image_url": f"http://localhost:8000/posters/{filename}"})


# =========================
# TEXTURE IMAGE ENDPOINT
# =========================

class TextureImageRequest(BaseModel):
    prompt: str
    size_px: int = 1024


@app.post("/api/texture-image")
def texture_image(req: TextureImageRequest):
    prompt = (req.prompt or "").strip() or "abstract pattern"
    size_px = max(256, min(1024, int(req.size_px or 1024)))

    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    filename = f"texture_{ts}.png"
    out_path = os.path.join(TEXTURE_DIR, filename)

    _make_ai_texture(prompt, out_path, size_px=size_px)
    return JSONResponse(content={"image_url": f"http://localhost:8000/textures/{filename}"})


# =========================
# LLM DECISION ENGINE
# =========================

def extract_json(text: str) -> dict:
    """Robustly extracts JSON from LLM output."""
    text = re.sub(r"```(?:json)?", "", text).strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
    match = re.search(r'\{[\s\S]*\}', text)
    if match:
        try:
            return json.loads(match.group())
        except json.JSONDecodeError:
            pass
    return {"commands": [{"action": "no_action", "reason": "LLM returned unparseable output"}]}


def llm_decide(transcript: str, gaze_target: str = "none") -> dict:
    """Sends transcript + gaze context to the LLM and returns a parsed commands dict."""
    if not transcript:
        return {"commands": [{"action": "no_action", "reason": "empty transcript"}]}

    user_message = f'GAZE_TARGET: "{gaze_target}"\nUSER SAID: "{transcript}"'

    try:
        response = client.chat.completions.create(
            model=LLM_MODEL,
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": user_message}
            ],
            max_completion_tokens=1000,
        )

        raw = response.choices[0].message.content.strip()

        # Ask LLM to explain its decision in plain English
        reasoning_response = client.chat.completions.create(
            model=LLM_MODEL,
            messages=[
                {"role": "system", "content": "You are a concise explainer. In 1-2 sentences, explain what action was chosen and why, based on the user's speech and gaze target. Be direct and plain — no JSON, no bullet points."},
                {"role": "user", "content": f'User said: "{transcript}"\nGaze target: "{gaze_target}"\nDecided commands: {raw}'}
            ],
            max_completion_tokens=150,
        )
        reasoning = reasoning_response.choices[0].message.content.strip()

        return {**extract_json(raw), "_reasoning": reasoning}

    except Exception as e:
        print(f"[LLM error]: {e}")
        return {"commands": [{"action": "no_action", "reason": f"LLM error: {str(e)}"}]}


# =========================
# TRANSCRIBE ENDPOINT
# =========================

@app.post("/transcribe")
async def transcribe(
    audio: UploadFile = File(...),
    gaze_target: Optional[str] = Form(default="none"),
):
    # Save uploaded audio
    with open(TEMP_AUDIO_PATH, "wb") as f:
        f.write(await audio.read())

    gaze_target = gaze_target.strip() if gaze_target else "none"
    print(f"[Gaze target]: '{gaze_target}'")

    # Whisper transcription (translates any language → English)
    t0 = time.time()
    segments, info = whisper_model.transcribe(TEMP_AUDIO_PATH, beam_size=5, task="translate")
    transcript = "".join(s.text for s in segments).strip()
    whisper_time = round(time.time() - t0, 3)
    print(f"[Whisper] ({whisper_time}s): '{transcript}'")

    # LLM decision
    t1 = time.time()
    result = llm_decide(transcript, gaze_target=gaze_target)
    llm_time = round(time.time() - t1, 3)

    commands = result.get("commands", [{"action": "no_action"}])
    reasoning = result.get("_reasoning", "")
    if not commands:
        commands = [{"action": "no_action"}]

    print("")
    print(f"[LLM] ({llm_time}s) → {json.dumps(commands)}")
    if reasoning:
        print(f"[Reasoning] {reasoning}")
    print("")

    # Log to file
    log_entry = {
        "time": datetime.now().isoformat(),
        "transcript": transcript,
        "gaze_target": gaze_target,
        "commands": commands,
        "whisper_ms": int(whisper_time * 1000),
        "llm_ms": int(llm_time * 1000),
    }
    with open(TRANSCRIPT_FILE, "a", encoding="utf-8") as f:
        f.write(json.dumps(log_entry) + "\n")

    return JSONResponse(content={
        "transcript": transcript,
        "gaze_target": gaze_target,
        "commands": commands,
        "command": commands[0],
        "meta": {
            "whisper_ms": log_entry["whisper_ms"],
            "llm_ms": log_entry["llm_ms"],
            "model": LLM_MODEL,
        }
    })


# =========================
# HEALTH CHECK
# =========================

@app.get("/health")
async def health():
    return {
        "status": "ok",
        "model": LLM_MODEL,
        "whisper": "small",
        "meshy": bool(MESHY_API_KEY),
        "openai_images": bool(OPENAI_API_KEY),
    }


# =========================
# RUN
# =========================

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
