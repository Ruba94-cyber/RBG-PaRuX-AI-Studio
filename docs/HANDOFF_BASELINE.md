# PaRuX handoff baseline

This repository began from an inherited Windows UI prototype on 2026-08-22.

At baseline:

- the WinUI source existed but had not produced a successful build;
- chat targeted an external OpenAI-compatible local endpoint directly;
- image, video, computer-use, and code-handoff routes were placeholders;
- there was no reusable PaRuX server/API boundary;
- runtime paths and hardware orchestration still required extraction into private local configuration.

The first milestone converts this prototype into a provider-based application:

```text
PaRuX Windows shell
        |
PaRuX Server / Core
        |
Provider adapters
```

The product goal is one uninterrupted chat experience. Provider processes remain
implementation details and are never exposed directly to remote clients.

