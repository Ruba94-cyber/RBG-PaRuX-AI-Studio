def price_basic(base_cents):
    discounted = base_cents - (base_cents * 5 + 50) // 100
    taxed = discounted + (discounted * 20 + 50) // 100
    return taxed


def price_plus(base_cents):
    discounted = base_cents - (base_cents * 10 + 50) // 100
    taxed = discounted + (discounted * 20 + 50) // 100
    return taxed


def price_pro(base_cents):
    discounted = base_cents - (base_cents * 15 + 50) // 100
    taxed = discounted + (discounted * 22 + 50) // 100
    return taxed
