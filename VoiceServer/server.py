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
from dotenv import load_dotenv

load_dotenv()
app = FastAPI()

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
client = OpenAI(api_key=OPENAI_API_KEY)
print("OPENAI_API_KEY loaded:", bool(OPENAI_API_KEY))

# ---- Paths (Unity persistentDataPath on Windows) ----
BASE_PATH = os.path.join(
    os.path.expanduser("~"),
    "AppData", "LocalLow", "DefaultCompany", "VRTApp-TestLocal"
)
os.makedirs(BASE_PATH, exist_ok=True)

TRANSCRIPT_FILE = os.path.join(BASE_PATH, "unity_transcripts.txt")
AUDIO_DIR = os.path.join(BASE_PATH, "recorded_audio")
os.makedirs(AUDIO_DIR, exist_ok=True)

MODEL_DIR = os.path.join(BASE_PATH, "GeneratedModels")
os.makedirs(MODEL_DIR, exist_ok=True)

app.mount("/files", StaticFiles(directory=MODEL_DIR), name="files")

POSTER_DIR = os.path.join(BASE_PATH, "posters")
os.makedirs(POSTER_DIR, exist_ok=True)

app.mount("/posters", StaticFiles(directory=POSTER_DIR), name="posters")

TEXTURE_DIR = os.path.join(BASE_PATH, "textures")
os.makedirs(TEXTURE_DIR, exist_ok=True)

app.mount("/textures", StaticFiles(directory=TEXTURE_DIR), name="textures")

# ---- Meshy config ----
MESHY_API_KEY = os.getenv("MESHY_API_KEY")
print("MESHY_API_KEY loaded:", bool(MESHY_API_KEY), flush=True)

MESHY_BASE = "https://api.meshy.ai"
MESHY_TEXT2_3D_CREATE = f"{MESHY_BASE}/openapi/v2/text-to-3d"
MESHY_TEXT2_3D_GET = f"{MESHY_BASE}/openapi/v2/text-to-3d"

jobs: Dict[str, Dict[str, Any]] = {}

# ---- Load Whisper model once ----
print("Loading Whisper model...", flush=True)
model = WhisperModel("small", device="cpu", compute_type="int8")
print("Whisper model loaded.", flush=True)


# =========================
# STARTUP EVENTS
# =========================

@app.on_event("startup")
def warmup_ollama():
    print("=" * 40, flush=True)
    print("[startup] Preloading llama3.2 model...", flush=True)
    print("=" * 40, flush=True)
    try:
        resp = requests.post(
            "http://localhost:11434/api/generate",
            json={
                "model": "llama3.2:latest",
                "prompt": 'Convert to JSON commands: "change the color to red"\nReturn: {"commands":[{"action":"set_color","target":"cube","color":"red"}]}',
                "stream": False,
                "keep_alive": "60m"
            },
            timeout=60
        )
        print(f"[startup] Ollama warmup complete. Status: {resp.status_code}", flush=True)
    except Exception as e:
        print("[startup] Ollama warmup failed:", e, flush=True)


@app.on_event("startup")
def _print_routes():
    print("=== Registered routes ===", flush=True)
    for r in app.routes:
        methods = getattr(r, "methods", None)
        print(f"{getattr(r, 'path', '')}  {methods}", flush=True)
    print("=========================", flush=True)


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
    # Fallback placeholder — do not delete
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
# UTILITY HELPERS
# =========================

def _safe_name(name: str) -> str:
    safe = re.sub(r"[^a-zA-Z0-9_\-]", "_", (name or "").strip())
    return safe if safe else "generated_model"


def _slug_to_name(s: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9]+", " ", (s or "")).strip()
    parts = [p.capitalize() for p in s.split() if p]
    if not parts:
        return "Generated_01"
    base = "".join(parts[:3])
    return f"{base}_01"


def _infer_object_phrase(text: str) -> str:
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
        " ", tl,
    )
    tl = re.sub(r"\s+", " ", tl).strip()
    return tl if tl else t


def _looks_like_example_leak(prompt: str, name: str) -> bool:
    p = (prompt or "").lower()
    n = (name or "").lower()
    return ("wooden chair" in p) or (n == "chair_01") or ("chair_01" in n)


# =========================
# MATERIAL INTENT HELPERS
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

WRITE_TEXT_PHRASES = ["write", "type", "print", "put text", "add text"]
MATERIAL_INTENT_PHRASES = ["turn it", "turn this", "turn that"]
GENERATION_OBJECT_WORDS = [
    "chair", "table", "sofa", "lamp", "ball", "cube", "box", "robot",
    "house", "car", "tree", "bottle", "phone", "mug", "cup", "vase", "poster"
]


# =========================
# FILLER / NOISE FILTER
# =========================

FILLER_WORDS = {
    "yes", "no", "ok", "okay", "yeah", "yep", "nope", "sure", "right",
    "hmm", "uh", "um", "ah", "oh", "eh", "huh", "hi", "hello",
    "bye", "thanks", "thank", "you", "please", "sorry", "what",
    "alright", "fine", "good", "great", "nice", "cool", "wow",
}

def _is_filler_transcript(text: str) -> bool:
    """Return True if the transcript is just noise/filler and not a real command."""
    cleaned = re.sub(r"[^a-z\s]", "", text.lower())
    words = [w for w in cleaned.split() if w]
    if not words:
        return True
    unique = set(words)
    non_fillers = unique - FILLER_WORDS
    # All words are filler words
    if not non_fillers:
        return True
    # Single unique word repeated 3+ times (e.g. "yes yes yes yes")
    if len(unique) == 1 and len(words) >= 3:
        return True
    # Two or fewer unique words, all filler (e.g. "yes yes ok ok")
    if len(unique) <= 2 and not non_fillers:
        return True
    return False


def _looks_like_write_text_request(text: str) -> bool:
    if not text:
        return False
    tl = text.lower()
    return ("wall" in tl) and any(p in tl for p in WRITE_TEXT_PHRASES)


def _extract_write_text_payload(text: str) -> Dict[str, Any]:
    t = (text or "").strip()
    msg = ""
    size = 60

    m = re.search(r"['\"]([^'\"]+)['\"]", t)
    if m:
        msg = m.group(1).strip()
    else:
        for sep in [".", ":", "-", "\u2014"]:
            if sep in t:
                parts = [p.strip() for p in t.split(sep) if p.strip()]
                if len(parts) >= 2:
                    msg = parts[-1].strip()
                    break
        if not msg:
            m2 = re.search(
                r"\b(?:write|type|print|put text|add text)\b\s+(.*?)\s+\b(?:on|to)\b\s+(?:the\s+)?wall\b",
                t, flags=re.IGNORECASE
            )
            if m2:
                msg = m2.group(1).strip()
        if not msg:
            msg = re.sub(
                r"^\s*(?:write|type|print|put text|add text)\s*(?:text\s*)?(?:on|to)\s*(?:this|the)?\s*wall\s*[:,\-\u2013\u2014]?\s*",
                "", t, flags=re.IGNORECASE
            ).strip()

    msg = re.sub(r"\b(on|to)\b\s+(?:this|the\s+)?wall\b", "", msg, flags=re.IGNORECASE).strip()
    msg = re.sub(r"\s+", " ", msg).strip()

    ms = re.search(r"\b(?:font\s*size|size)\s*(\d{2,3})\b", t, flags=re.IGNORECASE)
    if ms:
        try:
            size = int(ms.group(1))
        except Exception:
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
    if not text:
        return None
    tl = text.lower().strip()
    mat = _extract_material_keyword(tl)
    if not mat:
        return None
    has_material_intent_phrase = any(p in tl for p in MATERIAL_INTENT_PHRASES)
    has_pronoun_target = any(w in tl.split() for w in ["it", "this", "that"])
    mentions_object_noun = any(w in tl for w in GENERATION_OBJECT_WORDS)
    if (has_material_intent_phrase or has_pronoun_target) and not mentions_object_noun:
        return mat
    if "set material" in tl or "change material" in tl or "set texture" in tl or "change texture" in tl:
        return mat
    return None


# =========================
# POSTER INTENT HELPERS
# =========================

POSTER_PHRASES = [
    "image on the wall", "put an image", "put a picture", "hang a poster",
    "generate a poster", "create a poster", "make a poster", "wall poster",
    "put a poster", "add a poster", "place a poster"
    # ← removed bare "poster" — too broad, matches "move this poster"
]

MOVEMENT_WORDS = ["move", "translate", "shift", "push", "pull", "slide", "up", "down", "left", "right", "forward", "back"]

def _looks_like_poster_request(text: str) -> bool:
    if not text:
        return False
    tl = text.lower()
    # If it's a movement command, don't treat it as a poster creation request
    if any(w in tl for w in MOVEMENT_WORDS):
        return False
    return any(p in tl for p in POSTER_PHRASES)

def _extract_size_meters(text: str) -> Optional[Dict[str, float]]:
    if not text:
        return None
    tl = text.lower().replace("meters", "m").replace("meter", "m")
    m = re.search(r"(\d+(?:\.\d+)?)\s*m?\s*(?:x|by)\s*(\d+(?:\.\d+)?)\s*m", tl)
    if not m:
        m = re.search(r"(\d+(?:\.\d+)?)\s*(?:x|by)\s*(\d+(?:\.\d+)?)\s*m", tl)
    if not m:
        return None
    try:
        w = max(0.1, min(20.0, float(m.group(1))))
        h = max(0.1, min(20.0, float(m.group(2))))
        return {"width_m": w, "height_m": h}
    except Exception:
        return None


# =========================
# TEXTURE INTENT HELPERS
# =========================

TEXTURE_PHRASES = [
    "texture", "wall texture", "pattern", "tileable", "seamless",
    "make it look like", "cover the wall", "wallpaper"
]


def _looks_like_texture_request(text: str) -> bool:
    if not text:
        return False
    tl = text.lower()
    if any(p in tl for p in TEXTURE_PHRASES):
        return True
    if "wall" in tl and any(w in tl for w in ["bricks", "brick", "wood", "marble", "stone", "concrete", "flowers", "floral"]):
        return True
    return False


def _extract_texture_prompt(text: str) -> str:
    t = (text or "").strip()
    t = re.sub(r"\b(generate|create|make|set|change|apply|put|add|give)\b", " ", t, flags=re.I)
    t = re.sub(r"\b(a|an|the)\b", " ", t, flags=re.I)
    t = re.sub(r"\b(texture|pattern|material|wallpaper)\b", " ", t, flags=re.I)
    t = re.sub(r"\b(on|to|for)\b\s+(this|the)?\s*wall\b", " ", t, flags=re.I)
    t = re.sub(r"\bmake\b\s+it\s+look\s+like\b", " ", t, flags=re.I)
    t = re.sub(r"\s+", " ", t).strip()
    return t if t else "abstract pattern"


# =========================
# MESHY HELPERS
# =========================

def _meshy_headers() -> Dict[str, str]:
    if not MESHY_API_KEY:
        return {}
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
            raise RuntimeError("MESHY_API_KEY not set (use env var MESHY_API_KEY).")

        jobs[safe] = {
            "status": "RUNNING", "stage": stage, "task_id": None,
            "meshy_status": "PENDING", "progress": 0, "error": "",
        }

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

        if (stage or "").lower() == "preview":
            glb_url = preview_task["model_urls"]["glb"]
            _download_to_file(glb_url, out_glb)
            jobs[safe]["status"] = "DONE"
            jobs[safe]["meshy_status"] = "SUCCEEDED"
            jobs[safe]["progress"] = 100
            return

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

    except Exception as e:
        print("[meshy] ERROR:", e, flush=True)
        jobs[safe] = {
            "status": "ERROR", "stage": stage,
            "task_id": jobs.get(safe, {}).get("task_id"),
            "meshy_status": "FAILED", "progress": 0, "error": str(e),
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

    if os.path.exists(glb_path):
        return JSONResponse(status_code=200, content={
            "status": "SUCCEEDED", "progress": 100,
            "downloadUrl": f"http://localhost:8000/files/{glb_filename}",
        })

    job = jobs.get(safe)
    if job and job.get("status") == "RUNNING":
        return JSONResponse(status_code=202, content={
            "status": job.get("meshy_status", "IN_PROGRESS"),
            "progress": int(job.get("progress", 0) or 0),
        })

    if job and job.get("status") == "ERROR":
        return JSONResponse(status_code=500, content={
            "status": "FAILED", "progress": 0,
            "message": job.get("error", "Unknown error"),
        })

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
# Poster Image Endpoint
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

    # _make_placeholder_poster(prompt, out_path, w, h)  # fallback, do not delete
    _make_ai_poster(prompt, out_path, w, h)

    return JSONResponse(content={"image_url": f"http://localhost:8000/posters/{filename}"})


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
# Voice -> Commands
# =========================

def extract_commands(text: str) -> dict:

    overall_start = time.time()

    def _done(result: dict, label: str = "fast-path") -> dict:
        elapsed = time.time() - overall_start
        print(f"[extract] {label}: {elapsed:.3f}s", flush=True)
        return result

    def _extract_distance_meters(t: str, default: float = 0.2):
        tl = t.lower().strip()

        number_words = {
            "zero": 0.0,
            "a": 1.0,
            "an": 1.0,
            "one": 1.0,
            "two": 2.0,
            "three": 3.0,
            "four": 4.0,
            "five": 5.0,
            "six": 6.0,
            "seven": 7.0,
            "eight": 8.0,
            "nine": 9.0,
            "ten": 10.0,
            "half": 0.5,
        }

        cm_m = re.search(r"(\d+(?:\.\d+)?)\s*cm\b", tl)
        if cm_m:
            return float(cm_m.group(1)) * 0.01

        m_m = re.search(r"(\d+(?:\.\d+)?)\s*(?:meter|meters|metre|metres|m\b)", tl)
        if m_m:
            return float(m_m.group(1))

        word_unit_m = re.search(
            r"\b(zero|a|an|one|two|three|four|five|six|seven|eight|nine|ten|half)\b\s*"
            r"(?:meter|meters|metre|metres|m\b|centimeter|centimeters|centimetre|centimetres|cm\b)?",
            tl,
        )
        if word_unit_m:
            word = word_unit_m.group(1)
            value = number_words[word]
            if re.search(rf"\b{re.escape(word)}\b\s*(?:centimeter|centimeters|centimetre|centimetres|cm\b)", tl):
                return value * 0.01
            return value

        plain_m = re.search(r"(\d+(?:\.\d+)?)", tl)
        if plain_m:
            return float(plain_m.group(1))

        return default

    if not text or text.strip() == "":
        return _done({"commands": [{"action": "no_action"}]}, "empty-input")

    # ---- FILLER / NOISE FILTER ----
    if _is_filler_transcript(text):
        print(f"[extract] filler/noise transcript ignored: '{text}'", flush=True)
        return _done({"commands": [{"action": "no_action"}]}, "filler-input")

    # ---- FAST PATH: translate (reliable axis mapping, no LLM needed) ----
    def _try_translate_fast_path(t: str):
        tl = t.lower().strip()
        move_words = ["move", "translate", "shift", "push", "pull", "slide"]
        dir_words   = ["left", "right", "up", "down", "top", "bottom", "forward", "back", "backward", "front"]
        if not any(w in tl for w in move_words + dir_words):
            return None
        # Must have at least one direction word to be confident
        if not any(w in tl for w in dir_words):
            return None

        dist = _extract_distance_meters(tl, default=0.2)

        dx = dy = dz = 0.0
        # forward/back → dx  (forward=negative, backward=positive in this scene)
        if re.search(r"\b(forward|front|ahead)\b", tl):                     dx = -dist
        elif re.search(r"\b(back|backward|backwards|behind)\b", tl):        dx = dist
        # up/down → dy
        if re.search(r"\b(up|top|above|higher)\b", tl):                     dy = dist
        elif re.search(r"\b(down|bottom|below|lower)\b", tl):               dy = -dist
        # left/right → dz  (left=positive, right=negative)
        if re.search(r"\bleft\b", tl):                                      dz = dist
        elif re.search(r"\bright\b", tl):                                   dz = -dist

        if dx == 0.0 and dy == 0.0 and dz == 0.0:
            return None  # direction unclear — let LLM handle

        return {"commands": [{"action": "translate", "target": "cube",
                               "dx": dx, "dy": dy, "dz": dz}]}

    translate_result = _try_translate_fast_path(text)
    if translate_result:
        return _done(translate_result, "translate-fast-path")

    # ---- FAST PATH: scale ----
    def _try_scale_fast_path(t: str):
        tl = t.lower().strip()

        grow_words   = ["increase", "enlarge", "expand", "bigger", "larger",
                        "wider", "taller", "longer", "grow"]
        shrink_words = ["decrease", "reduce", "shrink", "smaller", "shorter",
                        "narrower", "thinner"]
        scale_trigger_words = grow_words + shrink_words + ["scale"]

        is_grow   = any(w in tl for w in grow_words)
        is_shrink = any(w in tl for w in shrink_words)

        # catch "scale up / scale down" via the word "scale" + direction hint
        if "scale" in tl:
            if re.search(r"\bscale\b.{0,30}\b(up|bigger|larger|taller|wider|longer|grow|more|out)\b", tl):
                is_grow = True
            elif re.search(r"\bscale\b.{0,30}\b(down|smaller|shorter|narrower|thinner|shrink|less|in)\b", tl):
                is_shrink = True

        # catch "make it bigger/smaller/taller/..." patterns
        if re.search(r"\bmake\b.{0,20}\b(bigger|larger|taller|wider|longer)\b", tl):
            is_grow = True
        if re.search(r"\bmake\b.{0,20}\b(smaller|shorter|narrower|thinner)\b", tl):
            is_shrink = True

        if not (is_grow or is_shrink):
            return None

        # Extract numeric distance if present
        dist = _extract_distance_meters(tl, default=None)

        # Determine axis from directional/dimensional keywords
        # X = forward/back/depth, Y = up/down/height/vertical, Z = left/right/width/horizontal
        axis = None

        # Explicit axis letters: "x direction", "x axis", "in x", "along x", etc.
        if re.search(r"\b(in\s+)?x[\s-]*(direction|axis|dir)?\b", tl):
            axis = "x"
        elif re.search(r"\b(in\s+)?y[\s-]*(direction|axis|dir)?\b", tl):
            axis = "y"
        elif re.search(r"\b(in\s+)?z[\s-]*(direction|axis|dir)?\b", tl):
            axis = "z"
        # Dimensional / directional words
        elif re.search(r"\bhorizontal\b", tl):
            axis = "z"
        elif re.search(r"\bvertical\b", tl):
            axis = "y"
        elif re.search(r"\b(width|wide|wider)\b", tl):
            axis = "z"
        elif re.search(r"\b(height|tall|taller)\b", tl):
            axis = "y"
        elif re.search(r"\b(depth|length|long|longer|forward|backward|back|front)\b", tl):
            axis = "x"
        elif re.search(r"\b(left|right)\b", tl):
            axis = "z"
        elif re.search(r"\b(up|down)\b", tl):
            axis = "y"

        sign = 1 if is_grow else -1

        if axis is not None:
            delta = sign * (dist if dist is not None else 0.2)
            return {"commands": [{"action": "scale", "target": "cube",
                                   "axis": axis, "delta": round(delta, 4)}]}
        elif dist is not None:
            # Distance given but no axis — fall through to LLM so it can infer intent
            print(f"[scale-fast-path] dist={dist} given but no axis inferred — letting LLM handle", flush=True)
            return None
        else:
            # No axis, no distance — uniform relative scale
            is_a_bit = bool(re.search(r"\b(a bit|slightly|a little|little|somewhat)\b", tl))
            factor = (1.2 if is_a_bit else 1.3) if is_grow else (0.8 if is_a_bit else 0.7)
            return {"commands": [{"action": "scale", "target": "cube",
                                   "factor": factor}]}

    scale_result = _try_scale_fast_path(text)
    if scale_result:
        return _done(scale_result, "scale-fast-path")

    if _looks_like_texture_request(text):
        user_tex = _extract_texture_prompt(text)
        return _done({"commands": [{
            "action": "set_wall_texture",
            "texture_prompt": f"seamless tileable texture of {user_tex}, realistic, no perspective, even lighting, no text",
            "tile_scale": 1.8
        }]}, "texture")

    mat = _looks_like_material_overlay_request(text)
    if mat:
        return _done({"commands": [{"action": "set_material", "target": "cube", "material": mat}]}, "material")

    if _looks_like_poster_request(text):
        size = _extract_size_meters(text) or {"width_m": 1.0, "height_m": 1.0}
        return _done({"commands": [{
            "action": "create_poster",
            "width_m": float(size["width_m"]),
            "height_m": float(size["height_m"]),
            "image_prompt": text
        }]}, "poster")

    if _looks_like_write_text_request(text):
        payload = _extract_write_text_payload(text)
        return _done({"commands": [{
            "action": "write_text",
            "text": payload["text"],
            "font_size": payload["font_size"]
        }]}, "write-text")

    # ---- LLM PATH ----

    desired_obj = _infer_object_phrase(text)
    desired_name = _slug_to_name(desired_obj)

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

7) translate (move relatively): {{"action":"translate","target":"cube","dx":0.0,"dy":0.2,"dz":0.0}}
   - Applies to ANY gazed object, including posters, models, and cubes.
   - AXIS RULES — always output all three fields (dx, dy, dz). Set unused axes to exactly 0.0.
   - forward/back → dx ONLY.  forward=NEGATIVE dx, backward=POSITIVE dx.  dy=0.0, dz=0.0
   - up/down      → dy ONLY.  up=positive dy,       down=negative dy.      dx=0.0, dz=0.0
   - left/right   → dz ONLY.  left=positive dz,     right=negative dz.     dx=0.0, dy=0.0
   - NEVER put a left/right value into dx. NEVER put a forward/back value into dz.
   Examples (all axes always shown):
   - "2 meters to the left"     -> dx=0.0,  dy=0.0,  dz=2.0
   - "2 meters to the right"    -> dx=0.0,  dy=0.0,  dz=-2.0
   - "1 meter forward"          -> dx=-1.0, dy=0.0,  dz=0.0
   - "1 meter back"             -> dx=1.0,  dy=0.0,  dz=0.0
   - "1 meter backwards"        -> dx=1.0,  dy=0.0,  dz=0.0
   - "50cm up"                  -> dx=0.0,  dy=0.5,  dz=0.0
   - "50cm down"                -> dx=0.0,  dy=-0.5, dz=0.0
   - If no distance given ("move a bit left", "slightly up", "move left") -> use 0.2 as distance.
   - POSTER MOVEMENT: "move the poster forward/back/left/right/up/down" -> translate

8) scale (two sub-cases only):
   {{"action":"scale","target":"cube","axis":"y","delta":1.0}}

   Choose exactly ONE sub-case:

   a) DIRECTIONAL (per-axis): user says "scale/stretch/extend/shrink/increase/decrease up/down/left/right/forward/back/horizontal/vertical"
      Fields: axis + delta
      axis mapping (X=forward/back, Y=up/down, Z=left/right/horizontal):
        up / upward / vertical / height / tall   -> axis="y", positive delta = grow
        down / downward                          -> axis="y", negative delta = shrink
        left / right / horizontal / width / wide -> axis="z", positive delta = grow
        forward / front / depth / length / long  -> axis="x", negative delta = grow (forward is -X)
        backward / back                          -> axis="x", positive delta = grow
      - "increase/enlarge/expand/bigger/wider/taller/longer" = grow (positive delta for y/z, negative for x-forward)
      - "decrease/reduce/shrink/smaller/shorter/narrower"    = shrink (negative delta)
      - If no distance given -> use 0.2 as delta.
      Examples:
        "scale up a bit"                          -> {{"action":"scale","target":"cube","axis":"y","delta":0.2}}
        "scale 1 meter upward"                    -> {{"action":"scale","target":"cube","axis":"y","delta":1.0}}
        "scale 50cm downward"                     -> {{"action":"scale","target":"cube","axis":"y","delta":-0.5}}
        "extend 2 meters to the right"            -> {{"action":"scale","target":"cube","axis":"z","delta":2.0}}
        "increase size horizontally by 10 meters" -> {{"action":"scale","target":"cube","axis":"z","delta":10.0}}
        "increase the width by 2 meters"          -> {{"action":"scale","target":"cube","axis":"z","delta":2.0}}
        "decrease the height by 1 meter"          -> {{"action":"scale","target":"cube","axis":"y","delta":-1.0}}
        "make it taller by 3 meters"              -> {{"action":"scale","target":"cube","axis":"y","delta":3.0}}

   b) RELATIVE UNIFORM: user says bigger/smaller/shrink/grow with NO specific direction
      Fields: factor only (no axis)
      factor < 1 = shrink, factor > 1 = grow
      - If no factor amount is implied ("a bit bigger", "slightly smaller") -> use factor=1.2 for grow, 0.8 for shrink.
      Examples:
        "make it a bit bigger" -> {{"action":"scale","target":"cube","factor":1.2}}
        "make it bigger"       -> {{"action":"scale","target":"cube","factor":1.3}}
        "shrink it a bit"      -> {{"action":"scale","target":"cube","factor":0.8}}
        "shrink it"            -> {{"action":"scale","target":"cube","factor":0.7}}

11) place_on_floor: {{"action":"place_on_floor","target":"cube"}}

12) generate_model:
   Use the user's requested object in BOTH prompt and name.
   Template:
   {{"action":"generate_model","prompt":"<OBJECT_FROM_USER>","name":"<ObjectName_01>","stage":"preview","art_style":"realistic"}}

13) create_poster:
   Create an image poster and place it on the gazed wall surface.
   Template:
   {{"action":"create_poster","width_m":2.0,"height_m":3.0,"image_prompt":"a vintage travel poster of Amsterdam canals"}}
   
14) run_code:
   If the user describes something they want an object TO DO
   (rotate, spin, walk, bounce, follow, orbit, animate, patrol, etc.),
   return:
   {{"action":"run_code","behaviour_prompt":"<clear technical restatement of the behaviour>","target":"<object name if mentioned, else empty string>"}}

Examples:
"make the cube rotate slowly"
-> {{"commands":[{{"action":"run_code","behaviour_prompt":"Rotate the selected cube continuously around the Y axis at a slow speed.","target":"cube"}}]}}

"make the human walk in a circle"
-> {{"commands":[{{"action":"run_code","behaviour_prompt":"Make the human walk continuously in a circle at a natural walking speed.","target":"human"}}]}}

"make it bounce"
-> {{"commands":[{{"action":"run_code","behaviour_prompt":"Make the selected object bounce up and down continuously.","target":""}}]}}

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
- If the user says "move the poster forward/back/left/right/up/down", use translate (NOT create_poster).

SCALING RULES (IMPORTANT):
- All scaling uses action="scale". Pick exactly one sub-case:
  a) Direction + number given ("1 meter upward/left/forward/etc.") -> use axis + delta fields
  b) No direction, no specific number ("bigger", "smaller")        -> use factor field only
- Never mix sub-cases (never include both axis and factor in the same command).

Distinguish carefully:
- One-time positional edits use translate.
Examples:
"make the cube rotate slowly"
-> {{"commands":[{{"action":"run_code","behaviour_prompt":"Rotate the selected cube continuously around the Y axis at a slow speed.","target":"cube"}}]}}

"make the human walk in a circle"
-> {{"commands":[{{"action":"run_code","behaviour_prompt":"Make the human walk continuously in a circle at a natural walking speed.","target":"human"}}]}}

"make it bounce"
-> {{"commands":[{{"action":"run_code","behaviour_prompt":"Make the selected object bounce up and down continuously.","target":""}}]}}


Rules:
- If there is no clear actionable edit — including if the user says filler words like
  "yes", "no", "ok", "yeah", "hmm", "uh", "thanks", or repeats a word —
  return ONLY: {{"commands":[{{"action":"no_action"}}]}}
- NEVER invent a command from ambiguous or non-command input.
- Output ONLY the JSON object. No explanation, no markdown, no extra text.

User: "{text}"
"""

    t_llm_start = time.time()

    try:
        resp = requests.post(
            "http://localhost:11434/api/generate",
            json={"model": "llama3.2:latest", "prompt": prompt, "stream": False, "keep_alive": "60m"},
            timeout=30
        )
        resp.raise_for_status()
        data = resp.json()
        raw = (data.get("response", "") or "").strip()

        print(f"[extract] LLM request time: {time.time() - t_llm_start:.3f}s", flush=True)

        m = re.search(r"\{.*\}", raw, flags=re.DOTALL)
        if not m:
            return _done({"commands": [{"action": "no_action"}]}, "llm-no-json")

        raw_json = m.group(0)

        try:
            obj = json.loads(raw_json)
        except json.JSONDecodeError:
            decoder = json.JSONDecoder()
            obj, _ = decoder.raw_decode(raw_json)

        if not isinstance(obj, dict) or "commands" not in obj or not isinstance(obj["commands"], list):
            return _done({"commands": [{"action": "no_action"}]}, "llm-bad-schema")

        normalized: List[Dict[str, Any]] = []
        run_code_dispatched = False  # allow only one run_code per response
        for c in obj["commands"]:
            if not isinstance(c, dict):
                continue

            action = str(c.get("action", "")).strip().lower()
            if not action:
                continue

            # --- run_code handler ---
            if action == "run_code":
                if run_code_dispatched:
                    print("[extract] Skipping duplicate run_code command.", flush=True)
                    continue
                behaviour_prompt = str(
                    c.get("behaviour_prompt", c.get("prompt", text)) or ""
                ).strip()

                target = str(c.get("target", "") or "").strip()

                if not behaviour_prompt:
                    c = {"action": "no_action"}
                    action = "no_action"
                else:
                    c = {
                        "action": "run_code",
                        "behaviour_prompt": behaviour_prompt,
                        "target": target
                    }
                    run_code_dispatched = True
                    
            if action in ("no_action", "noaction", "none"):
                action = "no_action"

            if action in ("set_color", "set_material", "move", "translate", "scale", "place_on_floor"):
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

                p_low = (c.get("prompt") or "").strip().lower()
                if p_low in MATERIAL_KEYWORDS or p_low in MATERIAL_ALIASES:
                    mat2 = _looks_like_material_overlay_request(text)
                    if mat2:
                        normalized.append({"action": "set_material", "target": "cube", "material": mat2})
                        continue

                p = c["prompt"].lower()
                if p in ("cube", "a cube", "generate a cube", "make a cube", "create a cube"):
                    c["prompt"] = "simple cube"
                    if c["name"].lower().startswith("generated"):
                        c["name"] = "Cube_01"

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
                try:
                    c["width_m"] = float(c.get("width_m", 1.0) or 1.0)
                except Exception:
                    c["width_m"] = 1.0
                try:
                    c["height_m"] = float(c.get("height_m", 1.0) or 1.0)
                except Exception:
                    c["height_m"] = 1.0
                c["width_m"] = max(0.1, min(20.0, c["width_m"]))
                c["height_m"] = max(0.1, min(20.0, c["height_m"]))
                c["image_prompt"] = str(c.get("image_prompt", c.get("prompt", "")) or "").strip()
                if not c["image_prompt"]:
                    c["image_prompt"] = text
                if "image_url" in c and c["image_url"] is not None:
                    c["image_url"] = str(c["image_url"]).strip()

            # Unified scale normalizer
            if action == "scale":
                axis = str(c.get("axis", "")).strip().lower()
                axis_aliases = {
                    "up": "y", "upward": "y", "down": "y", "downward": "y",
                    "vertical": "y", "height": "y",
                    "left": "z", "right": "z", "horizontal": "z", "width": "z",
                    "forward": "x", "front": "x", "backward": "x", "back": "x",
                    "depth": "x", "length": "x",
                }
                axis = axis_aliases.get(axis, axis)  # normalise word -> x/y/z

                if axis in ("x", "y", "z"):
                    # Sub-case a: directional
                    try:
                        delta = float(c.get("delta", 0.2))
                    except Exception:
                        delta = 0.2
                    raw_axis_word = str(c.get("axis", "")).strip().lower()
                    if raw_axis_word in ("down", "downward", "left", "backward", "back") and delta > 0:
                        delta = -delta
                    c = {"action": "scale", "target": c["target"], "axis": axis, "delta": delta}
                    if delta == 0.0:
                        action = "no_action"

                else:
                    # Sub-case b: relative uniform (factor only)
                    try:
                        factor = float(c.get("factor", 1.0))
                    except Exception:
                        factor = 1.0
                    c = {"action": "scale", "target": c["target"], "factor": factor}
                    if factor == 1.0:
                        action = "no_action"

                c["action"] = action

            c["action"] = action
            normalized.append(c)

        if not normalized:
            return _done({"commands": [{"action": "no_action"}]}, "llm-empty")

        # SAFETY: never let old action names through if LLM ignores the prompt
        normalized = [
            c for c in normalized
            if str(c.get("action", "")).strip().lower() not in ("set_scale", "scale_by", "scale_axis")
        ]

        return _done({"commands": normalized}, "llm")

    except Exception as e:
        print("Error calling LLM for commands:", e, flush=True)
        return _done({"commands": [{"action": "no_action"}]}, "llm-error")


# =========================
# Transcribe Endpoint
# =========================

@app.post("/transcribe")
async def transcribe(audio: UploadFile = File(...)):
    temp_path = os.path.join(BASE_PATH, "temp_recording.wav")

    with open(temp_path, "wb") as f:
        f.write(await audio.read())

    print(f"[{datetime.now().isoformat(timespec='seconds')}] Transcribing {temp_path}...", flush=True)

    t0 = time.time()
    segments, info = model.transcribe(temp_path, beam_size=5, task="translate")
    print(f"whisper: {time.time() - t0:.3f} sec", flush=True)

    text = "".join(segment.text for segment in segments).strip()
    print("Detected language:", info.language, "prob:", info.language_probability, flush=True)

    if not text:
        text = "[No speech recognized]"

    t1 = time.time()
    result = extract_commands(text)
    print(f"extract_commands: {time.time() - t1:.3f} sec", flush=True)

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


if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)