"""在 git push 被网络拦截时，用 GitHub Git Data API 同步整个仓库。

    python tools/push_api.py --repo LeastBit/Rou --branch main --message "..." --tag v1.0.0

- 以 `git ls-files` 为准（自动遵守 .gitignore），生成完整 tree 后提交，远端与本地严格一致
- 会顺带把 tag 强制指向新提交
- 凭据取自 git credential
"""

import argparse
import base64
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"


def token() -> str:
    out = subprocess.run(["git", "credential", "fill"],
                         input="protocol=https\nhost=github.com\n\n",
                         capture_output=True, text=True, check=True).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line[len("password="):].strip()
    raise SystemExit("未找到 GitHub 凭据")


def call(method: str, url: str, tok: str, payload=None):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + tok)
    req.add_header("Accept", "application/vnd.github+json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            return json.loads(resp.read().decode("utf-8") or "{}")
    except urllib.error.HTTPError as e:
        raise SystemExit(f"{method} {url} -> {e.code}: {e.read().decode('utf-8', 'ignore')[:300]}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="LeastBit/Rou")
    ap.add_argument("--branch", default="main")
    ap.add_argument("--message", required=True)
    ap.add_argument("--tag", default="")
    args = ap.parse_args()

    tok = token()
    files = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True).stdout.split()
    print(f"files: {len(files)}")

    # 1. 当前远端 HEAD
    try:
        ref = call("GET", f"{API}/repos/{args.repo}/git/ref/heads/{args.branch}", tok)
        parent = ref["object"]["sha"]
    except SystemExit:
        parent = None

    # 2. 逐个文件上传 blob
    entries = []
    for path in files:
        if not os.path.exists(path):
            continue
        raw = open(path, "rb").read()
        blob = call("POST", f"{API}/repos/{args.repo}/git/blobs", tok,
                    {"content": base64.b64encode(raw).decode("ascii"), "encoding": "base64"})
        entries.append({"path": path.replace("\\", "/"), "mode": "100644", "type": "blob", "sha": blob["sha"]})
        print("  blob", path, len(raw))

    # 3. tree + commit + 移动分支
    tree = call("POST", f"{API}/repos/{args.repo}/git/trees", tok, {"tree": entries})
    payload = {"message": args.message, "tree": tree["sha"]}
    if parent:
        payload["parents"] = [parent]
    commit = call("POST", f"{API}/repos/{args.repo}/git/commits", tok, payload)
    print("commit:", commit["sha"])

    if parent:
        call("PATCH", f"{API}/repos/{args.repo}/git/refs/heads/{args.branch}", tok,
             {"sha": commit["sha"], "force": True})
    else:
        call("POST", f"{API}/repos/{args.repo}/git/refs", tok,
             {"ref": f"refs/heads/{args.branch}", "sha": commit["sha"]})
    print("branch updated")

    if args.tag:
        try:
            call("PATCH", f"{API}/repos/{args.repo}/git/refs/tags/{args.tag}", tok,
                 {"sha": commit["sha"], "force": True})
            print("tag moved:", args.tag)
        except SystemExit as e:
            print("tag skip:", e)
    return 0


if __name__ == "__main__":
    sys.exit(main())
