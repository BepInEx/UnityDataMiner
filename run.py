import os
import subprocess
import requests
import os.path as p
import datetime
import string as s
import configparser


REPO_PATH = p.join(p.dirname(p.realpath(__file__)), "data")

try:
    c = configparser.ConfigParser()
    c.read(p.join(REPO_PATH, "runner.cfg"))
    config = c["runner"]
except:
    config = {}

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
                "timestamp": datetime.datetime.utcnow().isoformat(),
            }
        ],
    })


try:
    print(f"Started at {datetime.datetime.utcnow().isoformat()}")
    repo_path = p.join(REPO_PATH, "repo")
    unity_libs_path = p.join(repo_path, "libraries")
    old_libs = set(os.listdir(unity_libs_path))

    network = config.get("docker_network", None)
    result = subprocess.run(["docker", "run", "--rm", "-v", f"--network={network}" if network else "", f"{repo_path}:/data",
                            "-v", f"{p.join(REPO_PATH, 'config.toml')}:/app/config.toml", "unity-miner"], check=True)

    new_libs = set(os.listdir(unity_libs_path))

    diff = new_libs - old_libs
    if diff:
        push_discord(config.get("announce_discord_webhook", None), "30b563", "New Unity Libs",
                     announce_template.substitute({
                         "libs": "\n".join([f"* {v[:-3]}" for v in sorted(diff)]),
                         "lib_url": config.get("listing_url", "")
                     }))
except subprocess.CalledProcessError as ex:
    if ex.returncode != -1:
        push_discord(config.get("error_discord_webhook", None), "bd3d3d", "Error",
                     f"Unexpected error while running script: `{ex.returncode}`")
