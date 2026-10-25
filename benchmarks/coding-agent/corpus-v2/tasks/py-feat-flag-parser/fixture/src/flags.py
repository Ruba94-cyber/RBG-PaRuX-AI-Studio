def parse_args(argv):
    options = {}
    positionals = []
    for arg in argv:
        if arg.startswith("--"):
            options[arg[2:]] = True
        else:
            positionals.append(arg)
    return options, positionals
