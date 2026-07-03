"""
ocr_engine.py — PaddleOCR 引擎封装（单例模式）
"""

import threading
from typing import List, Dict, Any, Optional

class OCREngine:
    """
    PaddleOCR 单例封装
    避免重复加载模型（模型较大，加载耗时）
    """
    _instance: Optional["OCREngine"] = None
    _lock = threading.Lock()

    def __new__(cls):
        if cls._instance is None:
            with cls._lock:
                if cls._instance is None:
                    cls._instance = super().__new__(cls)
        return cls._instance

    def __init__(self, lang: str = "ch", use_gpu: bool = False):
        if hasattr(self, "_initialized"):
            return
        self._initialized = True
        self.lang = lang
        self.use_gpu = use_gpu
        self._ocr = None
        self._load_lock = threading.Lock()

    def load(self):
        """懒加载 OCR 引擎"""
        if self._ocr is not None:
            return self._ocr

        with self._load_lock:
            if self._ocr is not None:
                return self._ocr
            try:
                from paddleocr import PaddleOCR
                print("[OCREngine] 正在加载 PaddleOCR 模型（首次约 10s）...", flush=True)
                self._ocr = PaddleOCR(
                    use_angle_cls=True,
                    lang=self.lang,
                    show_log=False,
                    use_gpu=self.use_gpu,
                    det_db_thresh=0.3,
                    det_db_box_thresh=0.5,
                    det_db_unclip_ratio=1.5,
                )
                print("[OCREngine] 模型加载成功 ✓", flush=True)
            except Exception as e:
                print(f"[OCREngine] 模型加载失败: {e}", flush=True)
                raise
        return self._ocr

    def infer(self, image_bytes: bytes):
        """
        推理入口
        返回: List[{"text":..., "confidence":..., "bbox":[x1,y1,x2,y2]}]
        """
        import numpy as np
        from PIL import Image
        import io

        ocr = self.load()
        img = Image.open(io.BytesIO(image_bytes))
        img_np = np.array(img)

        result = ocr.ocr(img_np, cls=True)
        return self._parse_result(result)

    def _parse_result(self, raw) -> List[Dict[str, Any]]:
        """解析 PaddleOCR 原始输出为结构化 JSON"""
        lines: List[Dict[str, Any]] = []
        if not raw or not raw[0]:
            return lines

        for line in raw[0]:
            bbox_points = line[0]   # [[x1,y1], [x2,y2], [x3,y3], [x4,y4]]
            text, conf = line[1]

            xs = [p[0] for p in bbox_points]
            ys = [p[1] for p in bbox_points]

            lines.append({
                "text": text,
                "confidence": round(float(conf), 4),
                "bbox": [int(min(xs)), int(min(ys)), int(max(xs)), int(max(ys))]
            })

        # 按阅读顺序排序（先 y 再 x）
        lines.sort(key=lambda x: (x["bbox"][1], x["bbox"][0]))
        return lines


# 全局单例
_engine = OCREngine()

def get_engine() -> OCREngine:
    return _engine
