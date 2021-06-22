import os
import subprocess
import requests
import os.path as p
import datetime

ANNOUNCE_DISCORD_WEBHOOK = None
ERROR_DISCORD_WEBHOOK = "https://discord.com/api/webhooks/856947137568833556/Lvz2e5QRxG2KhrmN7xDay4WGayFyFt1z40Ev-0zBQOyKHYKz7AuwlnJKL-zLZ0Qq21ML"
REPO_PATH = p.join(p.dirname(p.realpath(__file__)), "data")


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
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{p.join(REPO_PATH, 'repo')}:/data",
                            "-v", f"{p.join(REPO_PATH, 'config.toml')}:/app/config.toml", "-it", "unity-miner"], check=True)
except subprocess.CalledProcessError as ex:
    if ex.returncode != -1:
        push_discord(ERROR_DISCORD_WEBHOOK, "bd3d3d", "Error", f"Unexpected error while running script: `{ex.returncode}`")