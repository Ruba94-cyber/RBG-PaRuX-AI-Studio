def format_money(cents, currency="USD"):
    whole, frac = divmod(cents, 100)
    return "%s %d.%02d" % (currency, whole, frac)
