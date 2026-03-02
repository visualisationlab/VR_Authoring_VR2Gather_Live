from fastapi import FastAPI, File, UploadFile, BackgroundTasks
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
import uvicorn
import os
from datetime import datetime
from faster_whisper import WhisperModel
import requests
import json
import re
import time
from typing import Optional, Dict, Any, List
from PIL import Image, ImageDraw
import base64
from openai import OpenAI
import io
import time
from dotenv import load_dotenv
import os

load_dotenv()
app = FastAPI()

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
client = OpenAI(api_key=OPENAI_API_KEY)
print("OPENAI_API_KEY loaded:", bool(OPENAI_API_KEY))

# ---- Paths (Unity persistentDataPath on Windows) ----
BASE_PATH = os.path.join(
    os.path.expanduser("~"),
    "AppData",
    "LocalLow",
    "DefaultCompany",
    "VRTApp-TestLocal"
)
os.makedirs(BASE_PATH, exist_ok=True)

# Transcript + temp audio will go here
TRANSCRIPT_FILE = os.path.join(BASE_PATH, "unity_transcripts.txt")
AUDIO_DIR = os.path.join(BASE_PATH, "recorded_audio")
os.makedirs(AUDIO_DIR, exist_ok=True)

# ---- 3D Model Output ----
MODEL_DIR = os.path.join(BASE_PATH, "generated_models")
os.makedirs(MODEL_DIR, exist_ok=True)

# Serve generated GLBs at: http://localhost:8000/files/<name>.glb
app.mount("/files", StaticFiles(directory=MODEL_DIR), name="files")

# ---- Poster Output ----
POSTER_DIR = os.path.join(BASE_PATH, "generated_posters")
os.makedirs(POSTER_DIR, exist_ok=True)

# Serve posters at: http://localhost:8000/posters/<name>.png
app.mount("/posters", StaticFiles(directory=POSTER_DIR), name="posters")

# ---- Texture Output ----
TEXTURE_DIR = os.path.join(BASE_PATH, "generated_textures")
os.makedirs(TEXTURE_DIR, exist_ok=True)

# Serve textures at: http://localhost:8000/textures/<name>.png
app.mount("/textures", StaticFiles(directory=TEXTURE_DIR), name="textures")

# ---- Meshy config ----
MESHY_API_KEY = os.getenv("MESHY_API_KEY")
print("MESHY_API_KEY loaded:", bool(MESHY_API_KEY), flush=True)

MESHY_BASE = "https://api.meshy.ai"
MESHY_TEXT2_3D_CREATE = f"{MESHY_BASE}/openapi/v2/text-to-3d"
MESHY_TEXT2_3D_GET = f"{MESHY_BASE}/openapi/v2/text-to-3d"  # + /{id}

# In-memory job tracking by "safe name"
jobs: Dict[str, Dict[str, Any]] = {}

# ---- Load Whisper model once ----
print("Loading Whisper model...", flush=True)
model = WhisperModel("small", device="cpu", compute_type="int8")
print("Whisper model loaded.", flush=True)

@app.on_event("startup")
def warmup_ollama():
    print("[startup] Preloading llama3.2 model...", flush=True)
    try:
        requests.post(
            "http://localhost:11434/api/generate",
            json={
                "model": "llama3.2:latest",
                "prompt": "warmup",
                "stream": False
            },
            timeout=60
        )
        print("[startup] Ollama warmup complete.", flush=True)
    except Exception as e:
        print("[startup] Ollama warmup failed:", e, flush=True)


def _make_ai_poster(prompt: str, out_path: str, w: int, h: int):
    """
    Generates an image and saves it to out_path as PNG.
    Note: many models support only fixed sizes; we generate 1024x1024 then resize.
    """
    # generate at a safe supported size
    result = client.images.generate(
        model="gpt-image-1",
        prompt=f"Poster artwork, high quality, no text unless requested. {prompt}",
        size="1024x1024",
    )

    img_bytes = base64.b64decode(result.data[0].b64_json)

    # optional: resize to requested w/h
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



def _safe_name(name: str) -> str:
    safe = re.sub(r"[^a-zA-Z0-9_\-]", "_", (name or "").strip())
    return safe if safe else "generated_model"


# ------------------------------
# robust name + object parsing
# ------------------------------
def _slug_to_name(s: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9]+", " ", (s or "")).strip()
    parts = [p.capitalize() for p in s.split() if p]
    if not parts:
        return "Generated_01"
    base = "".join(parts[:3])  # keep it short
    return f"{base}_01"


def _infer_object_phrase(text: str) -> str:
    """
    Best-effort extraction of the "thing" user wants to generate.
    Works even when LLM messes up by copying examples.
    """
    t = (text or "").strip()
    tl = t.lower()

    known_phrases = [
        "cricket ball", "volley ball", "volleyball", "soccer ball", "football",
        "basketball", "tennis ball", "golf ball"
    ]
    for kp in known_phrases:
        if kp in tl:
            return kp

    tl = re.sub(
        r"\b(please|for me|can you|could you|would you|i want|i need|make|create|generate|build|add|spawn|a|an|the|model|object|3d)\b",
        " ",
        tl,
    )
    tl = re.sub(r"\s+", " ", tl).strip()

    return tl if tl else t


def _looks_like_example_leak(prompt: str, name: str) -> bool:
    p = (prompt or "").lower()
    n = (name or "").lower()
    return ("wooden chair" in p) or (n == "chair_01") or ("chair_01" in n)


# =========================
# MATERIAL INTENT HELPERS  ✅ (prevents "generate_model: wood")
# =========================
MATERIAL_ALIASES = {
    "wooden": "wood",
    "steel": "metal",
    "iron": "metal",
    "gold": "metal",
    "silver": "metal",
    "copper": "metal",
    "bronze": "metal",
}

MATERIAL_KEYWORDS = {
    "wood", "wooden",
    "metal", "steel", "iron", "gold", "silver", "copper", "bronze",
    "glass",
    "marble", "stone", "concrete",
    "plastic", "rubber",
    "fabric", "cloth", "leather"
}

WRITE_TEXT_PHRASES = [
    "write", "type", "print", "put text", "add text"
]


# If user says these, they mean "apply to current object", not generate a new object
MATERIAL_INTENT_PHRASES = [
    #"set material", "change material", "apply material", "material to",
   #"set texture", "change texture", "apply texture", "texture to",
    #"make it", "make this", "make that",
    #"apply", "overlay", "cover", "coat",
    "turn it", "turn this", "turn that",
]

# Words that usually indicate a NEW object generation request (optional heuristic)
GENERATION_OBJECT_WORDS = [
    "chair", "table", "sofa", "lamp", "ball", "cube", "box", "robot",
    "house", "car", "tree", "bottle", "phone", "mug", "cup", "vase", "poster"
]

def _looks_like_write_text_request(text: str) -> bool:
    if not text:
        return False
    tl = text.lower()
    # wall intent + some write verb
    return ("wall" in tl) and any(p in tl for p in WRITE_TEXT_PHRASES)

def _extract_write_text_payload(text: str) -> Dict[str, Any]:
    t = (text or "").strip()

    # Default
    msg = ""
    size = 60

    # 1) Prefer quoted text
    m = re.search(r"['\"]([^'\"]+)['\"]", t)
    if m:
        msg = m.group(1).strip()
    else:
        # 2) If there is a clear separator, take what comes after it
        # Works for: "Write text on this wall. Hello Netherlands."
        #            "Write text on the wall: Hello Netherlands"
        #            "Write text on wall - Hello Netherlands"
        for sep in [".", ":", "-", "—"]:
            if sep in t:
                parts = [p.strip() for p in t.split(sep) if p.strip()]
                if len(parts) >= 2:
                    msg = parts[-1].strip()
                    break

        # 3) If still empty, try classic pattern: write <msg> on the wall
        if not msg:
            m2 = re.search(
                r"\b(?:write|type|print|put text|add text)\b\s+(.*?)\s+\b(?:on|to)\b\s+(?:the\s+)?wall\b",
                t,
                flags=re.IGNORECASE
            )
            if m2:
                msg = m2.group(1).strip()

        # 4) If STILL empty, remove leading instruction phrase if present
        if not msg:
            # remove "write text on this wall" / "write on the wall" etc. from start
            msg = re.sub(
                r"^\s*(?:write|type|print|put text|add text)\s*(?:text\s*)?(?:on|to)\s*(?:this|the)?\s*wall\s*[:,\-–—]?\s*",
                "",
                t,
                flags=re.IGNORECASE
            ).strip()

    # Cleanup extra words if they slipped in
    msg = re.sub(r"\b(on|to)\b\s+(?:this|the\s+)?wall\b", "", msg, flags=re.IGNORECASE).strip()
    msg = re.sub(r"\s+", " ", msg).strip()

    # Font size parsing
    ms = re.search(r"\b(?:font\s*size|size)\s*(\d{2,3})\b", t, flags=re.IGNORECASE)
    if ms:
        try:
            size = int(ms.group(1))
        except:
            size = 60
    size = max(10, min(200, size))

    if not msg:
        msg = "Hello World"

    return {"text": msg, "font_size": size}





def _extract_material_keyword(text: str) -> Optional[str]:
    if not text:
        return None
    tl = text.lower()
    for m in MATERIAL_KEYWORDS:
        if re.search(rf"\b{re.escape(m)}\b", tl):
            return MATERIAL_ALIASES.get(m, m)
    return None


def _looks_like_material_overlay_request(text: str) -> Optional[str]:
    """
    If user is clearly asking to change the texture/material of the CURRENT (gazed) object,
    return the material keyword. Otherwise return None.
    """
    if not text:
        return None

    tl = text.lower().strip()
    mat = _extract_material_keyword(tl)
    if not mat:
        return None

    has_material_intent_phrase = any(p in tl for p in MATERIAL_INTENT_PHRASES)
    has_pronoun_target = any(w in tl.split() for w in ["it", "this", "that"])
    mentions_object_noun = any(w in tl for w in GENERATION_OBJECT_WORDS)

    # If it sounds like "apply to existing" and doesn't clearly mention a new object => overlay
    if (has_material_intent_phrase or has_pronoun_target) and not mentions_object_noun:
        return mat

    # Explicit commands always mean overlay
    if "set material" in tl or "change material" in tl or "set texture" in tl or "change texture" in tl:
        return mat

    return None


# =========================
# POSTER INTENT HELPERS ✅
# =========================
POSTER_PHRASES = [
    "poster", "image on the wall", "put an image", "put a picture", "hang a poster",
    "generate a poster", "create a poster", "make a poster", "wall poster"
]

def _looks_like_poster_request(text: str) -> bool:
    if not text:
        return False
    tl = text.lower()
    return any(p in tl for p in POSTER_PHRASES)

def _extract_size_meters(text: str) -> Optional[Dict[str, float]]:
    """
    Extract sizes like:
    - "2m by 3m"
    - "2 by 3 meters"
    - "1 meter by 1 meter"
    - "2 x 3 m"
    Returns {"width_m": w, "height_m": h} or None.
    """
    if not text:
        return None
    tl = text.lower().replace("meters", "m").replace("meter", "m")

    # patterns like "2m by 3m", "2 by 3 m", "2 x 3m"
    m = re.search(r"(\d+(?:\.\d+)?)\s*m?\s*(?:x|by)\s*(\d+(?:\.\d+)?)\s*m", tl)
    if not m:
        m = re.search(r"(\d+(?:\.\d+)?)\s*(?:x|by)\s*(\d+(?:\.\d+)?)\s*m", tl)

    if not m:
        return None

    try:
        w = float(m.group(1))
        h = float(m.group(2))
        # sanity clamp
        w = max(0.1, min(20.0, w))
        h = max(0.1, min(20.0, h))
        return {"width_m": w, "height_m": h}
    except:
        return None


def _meshy_headers() -> Dict[str, str]:
    if not MESHY_API_KEY:
        return {}
    return {
        "Authorization": f"Bearer {MESHY_API_KEY}",
        "Accept": "application/json",
        "Content-Type": "application/json",
    }


def _meshy_create_task(mode: str, payload: Dict[str, Any]) -> str:
    """
    POST https://api.meshy.ai/openapi/v2/text-to-3d
    Returns task id in {"result": "<id>"}.
    """
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
    """
    GET https://api.meshy.ai/openapi/v2/text-to-3d/:id
    """
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
    # Keep ONLY what Unity needs: status + progress
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
            raise RuntimeError("MESHY_API_KEY not set (use env var MESHY_API_KEY).")

        # Initialize job
        jobs[safe] = {
            "status": "RUNNING",
            "stage": stage,
            "task_id": None,
            "meshy_status": "PENDING",
            "progress": 0,
            "error": "",
        }

        # 1) Create preview task
        preview_task_id = _meshy_create_task(
            mode="preview",
            payload={
                "prompt": prompt,
                "art_style": art_style,
                "should_remesh": True,
            },
        )

        _set_job_progress(safe, stage, preview_task_id, "PENDING", 0)

        # 2) Poll preview
        preview_task = None
        deadline = time.time() + 20 * 60  # 20 minutes
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

        # Preview-only -> download and finish
        if (stage or "").lower() == "preview":
            glb_url = preview_task["model_urls"]["glb"]
            _download_to_file(glb_url, out_glb)

            jobs[safe]["status"] = "DONE"
            jobs[safe]["meshy_status"] = "SUCCEEDED"
            jobs[safe]["progress"] = 100
            return

        # 3) Refine
        refine_task_id = _meshy_create_task(
            mode="refine",
            payload={
                "preview_task_id": preview_task_id,
                "enable_pbr": True,
            },
        )

        _set_job_progress(safe, stage, refine_task_id, "PENDING", 0)

        refine_task = None
        deadline = time.time() + 30 * 60  # 30 minutes
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

    except Exception as e:
        print("[meshy] ERROR:", e, flush=True)
        jobs[safe] = {
            "status": "ERROR",
            "stage": stage,
            "task_id": jobs.get(safe, {}).get("task_id"),
            "meshy_status": "FAILED",
            "progress": 0,
            "error": str(e),
        }


# =========================
# Text-to-3D Endpoint
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

    # Already generated?
    if os.path.exists(glb_path):
        return JSONResponse(
            status_code=200,
            content={
                "status": "SUCCEEDED",
                "progress": 100,
                "downloadUrl": f"http://localhost:8000/files/{glb_filename}",
            },
        )

    # If job exists, report ONLY status + progress (minimal)
    job = jobs.get(safe)
    if job and job.get("status") == "RUNNING":
        return JSONResponse(
            status_code=202,
            content={
                "status": job.get("meshy_status", "IN_PROGRESS"),
                "progress": int(job.get("progress", 0) or 0),
            },
        )

    if job and job.get("status") == "ERROR":
        return JSONResponse(
            status_code=500,
            content={
                "status": "FAILED",
                "progress": 0,
                "message": job.get("error", "Unknown error"),
            },
        )

    # Start new job
    stage = (req.stage or "preview").lower()
    art_style = (req.art_style or "realistic").lower()
    print(f"[text-to-3d] starting Meshy job name={req.name} safe={safe} stage={stage} art_style={art_style}", flush=True)

    jobs[safe] = {
        "status": "RUNNING",
        "stage": stage,
        "task_id": None,
        "meshy_status": "PENDING",
        "progress": 0,
        "error": "",
    }

    background_tasks.add_task(_generate_with_meshy_background, req.prompt, req.name, stage, art_style)

    return JSONResponse(
        status_code=202,
        content={"status": "PENDING", "progress": 0},
    )


# =========================
# Poster Image Endpoint (NEW)
# =========================

class PosterImageRequest(BaseModel):
    prompt: str
    width_px: int = 1024
    height_px: int = 1024

@app.post("/api/poster-image")
def poster_image(req: PosterImageRequest):
    """
    Returns:
      { "image_url": "http://localhost:8000/posters/poster_....png" }

    Currently generates a placeholder image with text.
    Later you can replace _make_placeholder_poster(...) with a call to Stable Diffusion / DALL·E / etc.
    """
    prompt = (req.prompt or "").strip()
    if not prompt:
        prompt = "A minimal poster"

    # clamp pixel sizes to avoid huge images
    w = max(256, min(2048, int(req.width_px or 1024)))
    h = max(256, min(2048, int(req.height_px or 1024)))

    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    filename = f"poster_{ts}.png"
    out_path = os.path.join(POSTER_DIR, filename)

#    _make_placeholder_poster(prompt, out_path, w, h)         ## dont delete it, I may need later to generate text
    _make_ai_poster(prompt, out_path, w, h)


    return JSONResponse(content={
        "image_url": f"http://localhost:8000/posters/{filename}"
    })
    
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

    return JSONResponse(content={
        "image_url": f"http://localhost:8000/textures/{filename}"
    })    


def _make_placeholder_poster(prompt: str, out_path: str, w: int, h: int):
    img = Image.new("RGB", (w, h), (245, 245, 245))
    draw = ImageDraw.Draw(img)

    # border
    draw.rectangle([8, 8, w - 8, h - 8], outline=(40, 40, 40), width=4)

    # simple wrapped text
    text = (prompt or "Poster").strip()
    text = text[:220]

    max_chars = 38 if w >= h else 30
    lines = [text[i:i + max_chars] for i in range(0, len(text), max_chars)]

    y = 40
    for ln in lines[:12]:
        draw.text((30, y), ln, fill=(20, 20, 20))
        y += 28

    img.save(out_path, "PNG")


# =========================
# Voice -> Commands
# =========================

# =========================
# TEXTURE INTENT HELPERS ✅
# =========================
TEXTURE_PHRASES = [
    "texture", "wall texture", "pattern", "tileable", "seamless",
    "make it look like", "cover the wall", "wallpaper"
]

def _looks_like_texture_request(text: str) -> bool:
    if not text:
        return False
    tl = text.lower()

    # must mention wall/texture-ish context
    if any(p in tl for p in TEXTURE_PHRASES):
        return True

    # common phrasing: "make this wall bricks", "turn this wall into marble"
    if "wall" in tl and any(w in tl for w in ["bricks", "brick", "wood", "marble", "stone", "concrete", "flowers", "floral"]):
        return True

    return False

def _extract_texture_prompt(text: str) -> str:
    t = (text or "").strip()

    # remove instruction words but keep the actual description
    t = re.sub(r"\b(generate|create|make|set|change|apply|put|add|give)\b", " ", t, flags=re.I)
    t = re.sub(r"\b(a|an|the)\b", " ", t, flags=re.I)
    t = re.sub(r"\b(texture|pattern|material|wallpaper)\b", " ", t, flags=re.I)
    t = re.sub(r"\b(on|to|for)\b\s+(this|the)?\s*wall\b", " ", t, flags=re.I)
    t = re.sub(r"\bmake\b\s+it\s+look\s+like\b", " ", t, flags=re.I)
    t = re.sub(r"\s+", " ", t).strip()

    return t if t else "abstract pattern"


def extract_commands(text: str) -> dict:

    overall_start = time.time()
    
    if not text or text.strip() == "":
        return {"commands": [{"action": "no_action"}]}
        
    # ✅ HARD OVERRIDE FIRST:
    # If user says "make it wood / set material to metal / change texture to marble"
    # we DO NOT call the LLM, so it never accidentally outputs generate_model("wood").
    
        
    tl = text.lower()
    
    
    if _looks_like_texture_request(text):
        user_tex = _extract_texture_prompt(text)
        return {"commands": [{
            "action": "set_wall_texture",
            "texture_prompt": f"seamless tileable texture of {user_tex}, realistic, no perspective, even lighting, no text",
            "tile_scale": 1.8
    }]}
    
    #if ("texture" in tl or "look like" in tl or "make" in tl):
    #    return {"commands":[{"action":"set_wall_texture","texture_prompt":"seamless tileable texture, realistic, no perspective","tile_scale":1.8}]}

    mat = _looks_like_material_overlay_request(text)
    if mat:
        return {"commands": [{"action": "set_material", "target": "cube", "material": mat}]}

    # ✅ HARD OVERRIDE FOR POSTERS (fast + reliable)
    # If user clearly asks for poster/image on wall, we can still call LLM (better prompts),
    # but this makes it work even if LLM fails.
    if _looks_like_poster_request(text):
        size = _extract_size_meters(text) or {"width_m": 1.0, "height_m": 1.0}
        # Use the full text as image prompt (good enough for MVP)
        return {"commands": [{
            "action": "create_poster",
            "width_m": float(size["width_m"]),
            "height_m": float(size["height_m"]),
            "image_prompt": text
        }]}
        
    if _looks_like_write_text_request(text):
        payload = _extract_write_text_payload(text)
        return {"commands": [{
            "action": "write_text",
            "text": payload["text"],
            "font_size": payload["font_size"]
        }]}        

    desired_obj = _infer_object_phrase(text)
    desired_name = _slug_to_name(desired_obj)
    
    t0 = time.time()
    

    prompt = f"""
You are an AI command generator for a Unity XR scene.

Task:
Convert the user's speech into ONE OR MORE JSON commands.
Return ONLY valid JSON. No extra text.

Output format (always):
{{
  "commands": [ ... ]
}}

Targets:
- Use "cube" when user says cube/block/box. (Target is mostly ignored because Unity uses gaze.)

Supported actions (you may output multiple in sequence):
1) lock: {{"action":"lock"}}
2) unlock: {{"action":"unlock"}}
3) stack_on: {{"action":"stack_on"}}
4) stack_on_next: {{"action":"stack_on_next"}}
5) set_color: {{"action":"set_color","target":"cube","color":"red"}}
6) set_material: {{"action":"set_material","target":"cube","material":"wood"}}
7) move: {{"action":"move","target":"cube","x":0.0,"y":1.0,"z":-0.5,"space":"world"}}
8) translate: {{"action":"translate","target":"cube","dx":0.0,"dy":0.2,"dz":0.0,"space":"world"}}
9) set_scale: {{"action":"set_scale","target":"cube","uniform":1.5}}
10) scale_by: {{"action":"scale_by","target":"cube","factor":0.7}}
11) place_on_floor: {{"action":"place_on_floor","target":"cube"}}

12) generate_model:
   Use the user's requested object in BOTH prompt and name.
   Template:
   {{"action":"generate_model","prompt":"<OBJECT_FROM_USER>","name":"<ObjectName_01>","stage":"preview","art_style":"realistic"}}

13) create_poster:
   Create an image poster and place it on the gazed wall surface.
   Template:
   {{"action":"create_poster","width_m":2.0,"height_m":3.0,"image_prompt":"a vintage travel poster of Amsterdam canals"}}

MODEL GENERATION RULES:
- Ignore the phrases "start recording" and "stop recording" if they appear.
- Use generate_model ONLY when the user asks to create a NEW object/model (e.g., "create a chair", "generate a table").
- If the user says "make it wood/metal/glass/marble" or "set material/texture to X", that is NOT generate_model.
  -> it must be set_material.

MATERIAL RULES:
- If user says: "make it wood/metal/glass/marble" or "set material to X" or "change texture to X"
  -> output: {{"action":"set_material","material":"X"}}
- Return only the material keyword (e.g., "wood", "metal", "glass", "marble").

POSTER RULES:
- If the user says: "create/generate/make a poster" or "put an image/picture on the wall"
  -> output create_poster.
- If user specifies size like "2m by 3m" or "1 by 1 meter", set width_m and height_m.
- If user doesn't specify size, default width_m=1.0 height_m=1.0
- The image_prompt should describe what the poster should show.
- DO NOT use set_material for posters.

SCALING RULES (IMPORTANT):
- If user specifies an absolute target size like "to 2.1" or "to 0.01" or "scale to X":
  -> output ONLY: {{"action":"set_scale","target":"cube","uniform": X}}
  -> DO NOT output scale_by in the same response.
- Use scale_by ONLY when user does NOT specify a numeric target.
  - If user says decrease/smaller/shrink => factor must be < 1 (e.g., 0.8)
  - If user says increase/bigger/grow => factor must be > 1 (e.g., 1.2)
- Never output both set_scale and scale_by for the same user utterance.

Rules:
- If there is no clear actionable edit, return:
  {{"commands":[{{"action":"no_action"}}]}}

User: "{text}"
"""

    t2 = time.time()
    
    try:
        resp = requests.post(
            "http://localhost:11434/api/generate",
            json={"model": "llama3.2:latest", "prompt": prompt, "stream": False},
            timeout=30
        )
        resp.raise_for_status()
        data = resp.json()
        raw = (data.get("response", "") or "").strip()
        
        t3 = time.time()
        print(f"[extract] LLM request time: {t3 - t2:.3f}s", flush=True)

        m = re.search(r"\{.*\}", raw, flags=re.DOTALL)
        if not m:
            return {"commands": [{"action": "no_action"}]}

        obj = json.loads(m.group(0))
        if not isinstance(obj, dict) or "commands" not in obj or not isinstance(obj["commands"], list):
            return {"commands": [{"action": "no_action"}]}

        normalized: List[Dict[str, Any]] = []
        for c in obj["commands"]:
            if not isinstance(c, dict):
                continue

            action = str(c.get("action", "")).strip().lower()
            if not action:
                continue

            if action in ("no_action", "noaction", "none"):
                action = "no_action"

            # Default target for gaze-based ops (Unity mostly ignores it, but we keep it consistent)
            if action in ("set_color", "set_material", "move", "translate", "set_scale", "scale_by", "place_on_floor"):
                c["target"] = str(c.get("target", "cube")).strip() or "cube"

            if action == "set_material":
                c["material"] = str(c.get("material", "")).strip()
                c["material"] = re.sub(r"[\,\.\!\?]+$", "", c["material"]).strip().lower()
                if c["material"] in MATERIAL_ALIASES:
                    c["material"] = MATERIAL_ALIASES[c["material"]]
                if not c["material"]:
                    action = "no_action"

            if action == "generate_model":
                c["prompt"] = str(c.get("prompt", "")).strip()
                c["name"] = str(c.get("name", "Generated_01")).strip() or "Generated_01"
                c["stage"] = str(c.get("stage", "preview")).strip().lower() or "preview"
                c["art_style"] = str(c.get("art_style", "realistic")).strip().lower() or "realistic"

                # ✅ SAFETY: if LLM tries generate_model("wood") etc, convert to set_material if transcript fits
                p_low = (c.get("prompt") or "").strip().lower()
                if p_low in MATERIAL_KEYWORDS or p_low in MATERIAL_ALIASES:
                    mat2 = _looks_like_material_overlay_request(text)
                    if mat2:
                        normalized.append({"action": "set_material", "target": "cube", "material": mat2})
                        continue

                # Special handling for "cube" request
                p = c["prompt"].lower()
                if p in ("cube", "a cube", "generate a cube", "make a cube", "create a cube"):
                    c["prompt"] = "simple cube"
                    if c["name"].lower().startswith("generated"):
                        c["name"] = "Cube_01"

                # Guard against example leakage / mismatch
                if _looks_like_example_leak(c["prompt"], c["name"]):
                    c["prompt"] = desired_obj
                    c["name"] = desired_name
                else:
                    bad_prompt = (c["prompt"] or "").lower()
                    tokens = [w for w in re.findall(r"[a-zA-Z]+", desired_obj.lower()) if len(w) > 2]
                    if tokens and not any(tok in bad_prompt for tok in tokens):
                        c["prompt"] = desired_obj
                        c["name"] = desired_name

                if not c["prompt"]:
                    action = "no_action"

            
            if action == "create_poster":
                # defaults
                try:
                    c["width_m"] = float(c.get("width_m", 1.0) or 1.0)
                except:
                    c["width_m"] = 1.0
                try:
                    c["height_m"] = float(c.get("height_m", 1.0) or 1.0)
                except:
                    c["height_m"] = 1.0

                # clamp meters
                c["width_m"] = max(0.1, min(20.0, c["width_m"]))
                c["height_m"] = max(0.1, min(20.0, c["height_m"]))

                c["image_prompt"] = str(c.get("image_prompt", c.get("prompt", "")) or "").strip()
                if not c["image_prompt"]:
                    c["image_prompt"] = text

                # Optional: LLM may include image_url; keep it if present
                if "image_url" in c and c["image_url"] is not None:
                    c["image_url"] = str(c["image_url"]).strip()

            c["action"] = action
            normalized.append(c)

        if not normalized:
            return {"commands": [{"action": "no_action"}]}

        # SAFETY FILTER: If LLM returns BOTH set_scale and scale_by, prefer set_scale and drop scale_by.
        has_set_scale = any(
            isinstance(c, dict) and str(c.get("action", "")).strip().lower() == "set_scale"
            for c in normalized
        )
        if has_set_scale:
            normalized = [
                c for c in normalized
                if str(c.get("action", "")).strip().lower() != "scale_by"
            ]

        return {"commands": normalized}

    except Exception as e:
        print("Error calling LLM for commands:", e, flush=True)
        return {"commands": [{"action": "no_action"}]}


@app.post("/transcribe")
async def transcribe(audio: UploadFile = File(...)):
    #temp_path = os.path.join(DESKTOP_PATH, "temp_recording.wav")
    temp_path = os.path.join(BASE_PATH, "temp_recording.wav")
    #temp_path = os.path.join(AUDIO_DIR, f"recording_{datetime.now().strftime('%Y%m%d_%H%M%S')}.wav")
    
    
    with open(temp_path, "wb") as f:
        f.write(await audio.read())

    print(f"[{datetime.now().isoformat(timespec='seconds')}] Transcribing {temp_path}...", flush=True)
    
    #segments, info = model.transcribe(temp_path, beam_size=5)         
    #text = "".join(segment.text for segment in segments).strip() 
    #print("Detected language:", info.language, "prob:", info.language_probability, flush=True)
    
    #if not text:
    #    text = "[No speech recognized]"

    #result = extract_commands(text)
    
    
    # ---- Whisper timing ----
    t0 = time.time()
    segments, info = model.transcribe(temp_path, beam_size=5)
    print("whisper:", time.time() - t0, "sec", flush=True)

    text = "".join(segment.text for segment in segments).strip()

    print("Detected language:", info.language, "prob:", info.language_probability, flush=True)

    if not text:
        text = "[No speech recognized]"

    # ---- Command extraction timing ----
    t1 = time.time()
    result = extract_commands(text)
    print("extract_commands:", time.time() - t1, "sec", flush=True)

    commands = result.get("commands", [{"action": "no_action"}])
    

    print("Transcript:", text, flush=True)
    print("Commands:", commands, flush=True)

    line = f"{datetime.now().isoformat(timespec='seconds')}: transcript={text} | commands={json.dumps(commands, ensure_ascii=False)}"
    with open(TRANSCRIPT_FILE, "a", encoding="utf-8") as f:
        f.write(line + "\n")

    return JSONResponse(content={
    "transcript": text,
    "detected_language": info.language,
    "language_probability": info.language_probability,
    "commands": commands,
    "command": commands[0] if commands else {"action": "no_action"}
    })


@app.on_event("startup")
def _print_routes():
    print("=== Registered routes ===", flush=True)
    for r in app.routes:
        methods = getattr(r, "methods", None)
        print(f"{getattr(r, 'path', '')}  {methods}", flush=True)
    print("=========================", flush=True)


if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
