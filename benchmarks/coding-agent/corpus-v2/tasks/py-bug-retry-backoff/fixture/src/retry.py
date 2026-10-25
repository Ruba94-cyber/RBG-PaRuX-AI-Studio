def retry(fn, attempts, base_delay, max_delay, sleep):
    last = None
    for n in range(1, attempts + 1):
        try:
            return fn()
        except Exception as exc:  # noqa: BLE001
            last = exc
            sleep(min(base_delay * n, max_delay))
    raise last
