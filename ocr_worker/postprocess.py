"""
postprocess.py — OCR 结果后处理
包含：去空行、断行修复、段落合并、标点符号修正
"""

import re
from typing import List, Dict, Any


def clean_lines(raw_lines: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """
    基础清洗：
    - 去除置信度极低的行（< 0.3）
    - 去除空行
    - 去除重复行（相邻完全相同）
    """
    cleaned = []
    prev_text = None
    for line in raw_lines:
        text = line["text"].strip()
        conf = line["confidence"]

        # 过滤低置信度
        if conf < 0.3:
            continue
        # 过滤空行
        if not text:
            continue
        # 去除相邻重复
        if text == prev_text:
            continue

        prev_text = text
        cleaned.append({**line, "text": text})

    return cleaned


def merge_paragraphs(lines: List[Dict[str, Any]], max_gap: int = 20) -> List[Dict[str, Any]]:
    """
    段落合并：
    如果两行 y 坐标间隔 < max_gap，认为是同一段落，合并文本
    """
    if not lines:
        return lines

    merged = [lines[0]]
    for i in range(1, len(lines)):
        curr = lines[i]
        prev = merged[-1]

        prev_bottom = prev["bbox"][3]
        curr_top = curr["bbox"][1]
        gap = curr_top - prev_bottom

        # 同一段落：间隔小 & 上一行不以句号/问号/叹号结尾
        if gap < max_gap and not re.search(r'[。！？\.\!\?]\s*$', prev["text"]):
            merged[-1] = {
                **prev,
                "text": prev["text"] + curr["text"],
                "bbox": [
                    prev["bbox"][0],
                    prev["bbox"][1],
                    max(prev["bbox"][2], curr["bbox"][2]),
                    max(prev["bbox"][3], curr["bbox"][3]),
                ]
            }
        else:
            merged.append(curr)

    return merged


def fix_punctuation(text: str) -> str:
    """
    常见标点符号修正（中英文混排场景）
    """
    fixes = [
        (r'，\s*', '，'),     # 逗号后去空格
        (r'。\s*', '。'),     # 句号后去空格
        (r'；\s*', '；'),     # 分号后去空格
        (r'：\s*', '：'),     # 冒号后去空格
        (r'（\s*', '（'),     # 左括号后去空格
        (r'\s*）', '）'),     # 右括号前去空格
        (r'(\d)，(\d)', r'\1,\2'),  # 数字间中文逗号 → 英文逗号
    ]
    for pattern, repl in fixes:
        text = re.sub(pattern, repl, text)
    return text


def postprocess(raw_lines: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """
    完整后处理流水线
    """
    lines = clean_lines(raw_lines)
    lines = merge_paragraphs(lines)

    for line in lines:
        line["text"] = fix_punctuation(line["text"])

    return lines
