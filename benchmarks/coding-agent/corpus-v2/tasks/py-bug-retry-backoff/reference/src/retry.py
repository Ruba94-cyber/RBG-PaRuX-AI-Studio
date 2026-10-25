def retry(fn, attempts, base_delay, max_delay, sleep):
    last = None
    for n in range(1, attempts + 1):
        try:
            return fn()
        except Exception as exc:  # noqa: BLE001
            last = exc
            if n < attempts:
                sleep(min(base_delay * 2 ** (n - 1), max_delay))
    raise last
