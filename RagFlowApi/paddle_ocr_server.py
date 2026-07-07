"""
PaddleOCR REST server — fallback OCR engine for DotsOcrClient.

Requirements:
    pip install paddlepaddle paddleocr fastapi uvicorn pillow numpy

Run:
    python paddle_ocr_server.py          # listens on 0.0.0.0:8800
    python paddle_ocr_server.py --port 9000 --lang vi

Then set in appsettings.json:
    "PaddleOcr": { "BaseUrl": "http://localhost:8800" }

API:
    POST /ocr
    Body JSON: { "image": "<base64 PNG or JPEG>", "mime_type": "image/png" }
    Response:  { "results": [{ "text": "...", "bbox": [x1,y1,x2,y2], "confidence": 0.99 }] }
"""

import argparse
import base64
import io
import logging
import numpy as np
from fastapi import FastAPI, HTTPException
from fastapi.responses import JSONResponse
from paddleocr import PaddleOCR
from PIL import Image
import uvicorn
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("paddle_ocr_server")

app = FastAPI(title="PaddleOCR Server")

# ── Global OCR instance (loads models once at startup) ────────────────────────
ocr_engine: PaddleOCR | None = None


class OcrRequest(BaseModel):
    image: str          # base64-encoded image bytes
    mime_type: str = "image/png"


@app.on_event("startup")
def load_model():
    global ocr_engine
    lang = app.state.lang if hasattr(app.state, "lang") else "vi"
    logger.info("Loading PaddleOCR (lang=%s) ...", lang)
    # use_angle_cls=True handles upside-down / rotated text
    ocr_engine = PaddleOCR(use_angle_cls=True, lang=lang, show_log=False)
    logger.info("PaddleOCR ready.")


@app.post("/ocr")
def run_ocr(req: OcrRequest):
    if ocr_engine is None:
        raise HTTPException(503, "OCR engine not ready")

    try:
        image_bytes = base64.b64decode(req.image)
        img = Image.open(io.BytesIO(image_bytes)).convert("RGB")
        img_array = np.array(img)
    except Exception as e:
        raise HTTPException(400, f"Cannot decode image: {e}")

    try:
        raw = ocr_engine.ocr(img_array, cls=True)
    except Exception as e:
        logger.error("OCR failed: %s", e)
        raise HTTPException(500, f"OCR error: {e}")

    results = []
    # raw is a list of pages; we always send one image at a time
    for page in (raw or []):
        for line in (page or []):
            # line = [[[x1,y1],[x2,y1],[x2,y2],[x1,y2]], (text, confidence)]
            quad, (text, confidence) = line
            if not text or not text.strip():
                continue
            # Convert 4-corner quad to axis-aligned bbox [x1, y1, x2, y2]
            xs = [pt[0] for pt in quad]
            ys = [pt[1] for pt in quad]
            bbox = [int(min(xs)), int(min(ys)), int(max(xs)), int(max(ys))]
            results.append({
                "text":       text.strip(),
                "bbox":       bbox,
                "confidence": round(float(confidence), 4),
            })

    logger.info("OCR done: %d text blocks found.", len(results))
    return JSONResponse({"results": results})


@app.get("/health")
def health():
    return {"status": "ok", "engine": "paddleocr", "ready": ocr_engine is not None}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--host",  default="0.0.0.0")
    parser.add_argument("--port",  type=int, default=8800)
    parser.add_argument("--lang",  default="vi", help="PaddleOCR language code (vi, en, ch, ...)")
    args = parser.parse_args()

    app.state.lang = args.lang
    uvicorn.run(app, host=args.host, port=args.port)
