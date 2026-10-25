def parse_args(argv):
    options = {}
    positionals = []

    def add(key, value):
        if key in options:
            current = options[key]
            if isinstance(current, list):
                current.append(value)
            else:
                options[key] = [current, value]
        else:
            options[key] = value

    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--":
            positionals.extend(argv[i + 1:])
            break
        if arg.startswith("--"):
            body = arg[2:]
            if "=" in body:
                key, value = body.split("=", 1)
                add(key, value)
            elif i + 1 < len(argv) and not argv[i + 1].startswith("--"):
                add(body, argv[i + 1])
                i += 1
            else:
                add(body, True)
        else:
            positionals.append(arg)
        i += 1
    return options, positionals
