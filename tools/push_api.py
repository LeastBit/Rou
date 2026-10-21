"""在 git push 被网络拦截时，用 GitHub Git Data API 同步仓库（只推送有变化的文件）。

    python tools/push_api.py --repo LeastBit/Rou --branch main --message "..." [--tag v1.0.0]

- 用 `git hash-object` 与远端 tree 比对，仅上传差异文件
- 以远端 tree 为 base_tree 提交，未变化的文件自动继承
- 会顺带把 tag 强制指向新提交
- 凭据取自 git credential
"""

import argparse
import base64
import json
import subprocess
import sys
import time
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


def call(method: str, url: str, tok: str, payload=None, retries: int = 4):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    last = None
    for attempt in range(retries):
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Authorization", "Bearer " + tok)
        req.add_header("Accept", "application/vnd.github+json")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=120) as resp:
                return json.loads(resp.read().decode("utf-8") or "{}")
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "ignore")[:300]
            if e.code in (502, 503, 504) and attempt + 1 < retries:
                last = f"{e.code} {detail}"
                time.sleep(2 * (attempt + 1))
                continue
            raise SystemExit(f"{method} {url} -> {e.code}: {detail}")
        except Exception as e:  # 网络抖动 / SSL EOF
            last = str(e)
            time.sleep(2 * (attempt + 1))
    raise SystemExit(f"{method} {url} failed after {retries} tries: {last}")


def local_sha(path: str) -> str:
    return subprocess.run(["git", "hash-object", path], capture_output=True,
                          text=True, check=True).stdout.strip()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default="LeastBit/Rou")
    ap.add_argument("--branch", default="main")
    ap.add_argument("--message", required=True)
    ap.add_argument("--tag", default="")
    args = ap.parse_args()

    tok = token()
    files = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True).stdout.split()

    # 远端当前状态
    parent = None
    base_tree = None
    remote = {}
    try:
        ref = call("GET", f"{API}/repos/{args.repo}/git/ref/heads/{args.branch}", tok)
        parent = ref["object"]["sha"]
        commit = call("GET", f"{API}/repos/{args.repo}/git/commits/{parent}", tok)
        base_tree = commit["tree"]["sha"]
        tree = call("GET", f"{API}/repos/{args.repo}/git/trees/{base_tree}?recursive=1", tok)
        remote = {t["path"]: t["sha"] for t in tree.get("tree", []) if t["type"] == "blob"}
    except SystemExit:
        parent = None

    entries = []
    for path in files:
        rel = path.replace("\\", "/")
        sha = local_sha(path)
        if remote.get(rel) == sha:
            continue
        raw = open(path, "rb").read()
        blob = call("POST", f"{API}/repos/{args.repo}/git/blobs", tok,
                    {"content": base64.b64encode(raw).decode("ascii"), "encoding": "base64"})
        entries.append({"path": rel, "mode": "100644", "type": "blob", "sha": blob["sha"]})
        print(f"  + {rel} ({len(raw)} B)")

    if not entries:
        print("nothing to push")
        return 0

    payload_tree = {"tree": entries}
    if base_tree:
        payload_tree["base_tree"] = base_tree
    new_tree = call("POST", f"{API}/repos/{args.repo}/git/trees", tok, payload_tree)

    payload = {"message": args.message, "tree": new_tree["sha"]}
    if parent:
        payload["parents"] = [parent]
    new_commit = call("POST", f"{API}/repos/{args.repo}/git/commits", tok, payload)
    print("commit:", new_commit["sha"])

    if parent:
        call("PATCH", f"{API}/repos/{args.repo}/git/refs/heads/{args.branch}", tok,
             {"sha": new_commit["sha"], "force": True})
    else:
        call("POST", f"{API}/repos/{args.repo}/git/refs", tok,
             {"ref": f"refs/heads/{args.branch}", "sha": new_commit["sha"]})
    print("branch updated:", args.branch)

    if args.tag:
        try:
            call("PATCH", f"{API}/repos/{args.repo}/git/refs/tags/{args.tag}", tok,
                 {"sha": new_commit["sha"], "force": True})
            print("tag moved:", args.tag)
        except SystemExit as e:
            print("tag skip:", e)
    return 0


if __name__ == "__main__":
    sys.exit(main())
