import os
import subprocess
import requests
import os.path as p
import datetime
import string as s
import configparser
import jinja2
import re


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


def split_num(s):
    # Split strings like
    # "0a1" -> (0, "a", 1)
    # "0b1" -> (0, "b", 1)
    # "0b12" -> (0, "b", 12)
    # "0b123" -> (0, "b", 123)

    pattern = r"(?P<num>\d+)(?P<letter>[a-zA-Z])?(?P<rest>\d*)?"
    m = re.match(pattern, s)
    if not m:
        return (0, s, 0)
    num = int(m.group("num"))
    letter = m.group("letter") or ""
    rest = int(m.group("rest") or 0)
    return (num, letter, rest)


def update_index(repo_path, new_libs):
    env = jinja2.Environment(
        loader=jinja2.FileSystemLoader(repo_path),
        autoescape=jinja2.select_autoescape(['html', 'xml']),
    )
    index_template = env.get_template("index.jinja2")
    now = datetime.datetime.utcnow().strftime("%b %d, %Y at %I:%M:%S %p %Z")
    libs = [p.splitext(p.basename(lib))[0] for lib in new_libs]
    libs = sorted(libs, key=lambda x: split_num(x.split(".")[2])[2])
    libs = sorted(libs, key=lambda x: split_num(x.split(".")[2])[1])
    libs = sorted(libs, key=lambda x: split_num(x.split(".")[2])[0])
    libs = sorted(libs, key=lambda x: int(x.split(".")[1]))
    libs = sorted(libs, key=lambda x: int(x.split(".")[0]))
    index_html = index_template.render(libs=libs, now=now)
    with open(p.join(repo_path, "index.html"), "w") as f:
        f.write(index_html)


try:
    print(f"Started at {datetime.datetime.utcnow().isoformat()}")
    repo_path = p.join(REPO_PATH, "repo")
    unity_libs_path = p.join(repo_path, "libraries")
    old_libs = set(os.listdir(unity_libs_path))

    network = config.get("docker_network", None)
    result = subprocess.run(["docker", "run", "--rm", f"--network={network}" if network else "", "-v", f"{repo_path}:/data",
                            "-v", f"{p.join(REPO_PATH, 'config.toml')}:/app/config.toml", "unity-miner"], check=True)

    new_libs = set(os.listdir(unity_libs_path))

    diff = new_libs - old_libs
    if diff:
        push_discord(config.get("announce_discord_webhook", None), "30b563", "New Unity Libs",
                     announce_template.substitute({
                         "libs": "\n".join([f"* {v[:-3]}" for v in sorted(diff)]),
                         "lib_url": config.get("listing_url", "")
                     }))
        update_index(repo_path, new_libs)

except subprocess.CalledProcessError as ex:
    if ex.returncode != -1:
        push_discord(config.get("error_discord_webhook", None), "bd3d3d", "Error",
                     f"Unexpected error while running script: `{ex.returncode}`")
