import re


def slugify(text):
    out = re.sub(r"[^a-z0-9]+", "-", text.lower())
    return out.strip("-")
