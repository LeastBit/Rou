"""发布 Rou 到 GitHub Releases（无需 gh CLI）。

用法：
    python tools/release.py --tag v1.0.0 --name "Rou 1.0.0" --notes-file RELEASE.md \
        --asset dist/Rou.exe=Rou-1.0.0.exe --asset dist/standalone/Rou.exe=Rou-1.0.0-standalone.exe

凭据取自 git credential（与 git push 用同一份），不会写入磁盘。
"""

import argparse
import json
import mimetypes
import os
import subprocess
import sys
import urllib.error
import urllib.request

OWNER_REPO = "LeastBit/Rou"
API = "https://api.github.com"


def token() -> str:
    out = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True,
        text=True,
        check=True,
    ).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line[len("password="):].strip()
    raise SystemExit("未找到 GitHub 凭据，请先执行一次 git push 登录")


def call(method: str, url: str, tok: str, data: bytes | None = None, ctype: str = "application/json"):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + tok)
    req.add_header("Accept", "application/vnd.github+json")
    if data is not None:
        req.add_header("Content-Type", ctype)
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            return json.loads(resp.read().decode("utf-8") or "{}")
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "ignore")
        raise SystemExit(f"{method} {url} -> {e.code}: {detail[:400]}")


def ensure_release(tok: str, tag: str, name: str, notes: str) -> dict:
    try:
        return call("GET", f"{API}/repos/{OWNER_REPO}/releases/tags/{tag}", tok)
    except SystemExit:
        pass
    payload = json.dumps({
        "tag_name": tag,
        "target_commitish": "main",
        "name": name,
        "body": notes,
        "draft": False,
        "prerelease": False,
    }).encode("utf-8")
    return call("POST", f"{API}/repos/{OWNER_REPO}/releases", tok, payload)


def upload(tok: str, release_id: int, path: str, asset_name: str) -> None:
    size = os.path.getsize(path)
    existing = call("GET", f"{API}/repos/{OWNER_REPO}/releases/{release_id}/assets", tok)
    for a in existing:
        if a.get("name") == asset_name:
            call("DELETE", f"{API}/repos/{OWNER_REPO}/releases/assets/{a['id']}", tok)
            break
    ctype = mimetypes.guess_type(asset_name)[0] or "application/octet-stream"
    with open(path, "rb") as fh:
        data = fh.read()
    print(f"  uploading {asset_name} ({size/1024/1024:.1f} MB) ...", flush=True)
    res = call(
        "POST",
        f"https://uploads.github.com/repos/{OWNER_REPO}/releases/{release_id}/assets?name={asset_name}",
        tok,
        data,
        ctype,
    )
    print(f"  ✓ {res.get('name')} {res.get('size', 0)/1024/1024:.1f} MB", flush=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--notes-file", required=True)
    ap.add_argument("--asset", action="append", default=[], help="源路径=发布名")
    args = ap.parse_args()

    notes = open(args.notes_file, encoding="utf-8").read()
    tok = token()
    rel = ensure_release(tok, args.tag, args.name, notes)
    print("release:", rel.get("html_url"))
    for spec in args.asset:
        src, _, name = spec.partition("=")
        upload(tok, int(rel["id"]), src, name or os.path.basename(src))
    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
