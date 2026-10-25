from datetime import date


def days_inclusive(start, end):
    if end < start:
        raise ValueError("end before start")
    return (end - start).days + 1
