import os
import subprocess
import requests
import os.path as p
import datetime
import string as s

ANNOUNCE_DISCORD_WEBHOOK = None
ERROR_DISCORD_WEBHOOK = None
REPO_PATH = p.join(p.dirname(p.realpath(__file__)), "data")
LISTING_URL = ""

announce_template = s.Template(
    """
New Unity libraries available:
$libs
View all libraries at $lib_url.
""")


def push_discord(url, color, title, msg):
    if not url:
        return

    requests.post(url, json={
        "embeds": [
            {
                "title": title,
                "color": int(color, 16),
                "description": msg,
                "timestamp": datetime.datetime.now().isoformat(),
            }
        ],
    })


try:
    repo_path = p.join(REPO_PATH, "repo")
    unity_libs_path = p.join(repo_path, "libraries")
    old_libs = set(os.listdir(unity_libs_path))

    result = subprocess.run(["docker", "run", "--rm", "-v", f"{repo_path}:/data",
                            "-v", f"{p.join(REPO_PATH, 'config.toml')}:/app/config.toml", "-it", "unity-miner"], check=True)

    new_libs = set(os.listdir(unity_libs_path))

    diff = new_libs - old_libs
    if diff:
        push_discord(ANNOUNCE_DISCORD_WEBHOOK, "30b563", "New Unity Libs",
                     announce_template.substitute({
                         "libs": "\n".join([f"* {v[:-3]}" for v in sorted(diff)]),
                         "lib_url": LISTING_URL
                     }))
except subprocess.CalledProcessError as ex:
    if ex.returncode != -1:
        push_discord(ERROR_DISCORD_WEBHOOK, "bd3d3d", "Error",
                     f"Unexpected error while running script: `{ex.returncode}`")
