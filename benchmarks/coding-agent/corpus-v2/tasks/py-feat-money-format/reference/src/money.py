def format_money(cents, currency="USD"):
    negative = cents < 0
    whole, frac = divmod(abs(cents), 100)
    text = "%s %s.%02d" % (currency, format(whole, ","), frac)
    return "(" + text + ")" if negative else text
