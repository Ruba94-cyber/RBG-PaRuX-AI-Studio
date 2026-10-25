def _apply_pricing(base_cents, discount_percent, tax_percent):
    discounted = base_cents - (base_cents * discount_percent + 50) // 100
    return discounted + (discounted * tax_percent + 50) // 100


def price_basic(base_cents):
    return _apply_pricing(base_cents, 5, 20)


def price_plus(base_cents):
    return _apply_pricing(base_cents, 10, 20)


def price_pro(base_cents):
    return _apply_pricing(base_cents, 15, 22)
