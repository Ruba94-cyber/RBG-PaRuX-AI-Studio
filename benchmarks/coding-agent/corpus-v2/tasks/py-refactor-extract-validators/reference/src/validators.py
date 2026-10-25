def is_email(value):
    if not isinstance(value, str) or value.count("@") != 1:
        return False
    local, domain = value.split("@")
    return bool(local) and "." in domain and not domain.startswith(".") and not domain.endswith(".")


def is_port(value):
    return isinstance(value, int) and not isinstance(value, bool) and 1 <= value <= 65535
