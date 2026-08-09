"""生成条件とハッシュの記録.

RAW ファイルだけが残ると、後から「どの条件で作ったのか」が分からなくなります。
manifest は画像と対で残すためのもので、**コメントを含まない素の JSON** です
（機械が読み直すことを優先し、人向けの説明は JSONC 側の設定に置きます）。
"""

from __future__ import annotations

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import __version__
from .config import Config


def sha256_file(path: str | Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def build(
    cfg: Config,
    outputs: dict[str, str],
    raw_size: int,
    relative_to: str | Path,
    extra: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """manifest の中身を組み立てる.

    ``files[].path`` はmanifestファイルの置かれるディレクトリからの相対パスで記録する。
    """
    params = cfg.to_dict()
    params.pop("output", None)
    params.pop("outputs", None)

    files = []
    for kind, path in sorted(outputs.items()):
        p = Path(path)
        relative_path = p.resolve().relative_to(Path(relative_to).resolve()).as_posix()
        files.append(
            {
                "kind": kind,
                "path": relative_path,
                "bytes": p.stat().st_size if p.exists() else None,
                "sha256": sha256_file(p) if p.exists() else None,
            }
        )

    doc: dict[str, Any] = {
        "manifest_version": 1,
        "generator": {"name": "video-test-patterns", "version": __version__},
        "generated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "parameters": params,
        "parameters_sha256": sha256_bytes(
            json.dumps(params, sort_keys=True, ensure_ascii=False).encode("utf-8")
        ),
        "raw_bytes": raw_size,
        "files": files,
    }
    if extra:
        doc.update(extra)
    return doc


def write(doc: dict[str, Any], path: str | Path) -> None:
    Path(path).write_text(
        json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
