from validators import is_email, is_port


def register(email, port):
    if not is_email(email):
        return {"ok": False, "error": "email"}
    if not is_port(port):
        return {"ok": False, "error": "port"}
    return {"ok": True, "email": email.lower(), "port": port}
