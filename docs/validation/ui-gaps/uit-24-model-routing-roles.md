# UIT-24 Model Routing roles and explanations: UI gaps

## Stable AutomationId gap

| Control | Page | Suggested AutomationId |
| --- | --- | --- |
| Individual role options inside the role selector | Model Intelligence (Model Routing) | `ModelRouting_Role_<role>_Item` |

The role ComboBox has a stable AutomationId, but its options are populated as strings. The tests discover each option by `ControlType.ListItem` and its UIA name.

## Behaviors not asserted

- The role status is role-specific only when its candidate list is empty. When candidates exist, it reports only a generic candidate count (`src/PaRuX.App/Views/ModelRoutingPanel.cs:346-347`). Coverage checks the role-specific empty message and that a positive status has visible candidate rows; the current non-empty text cannot prove which role produced that count.
- `DescribeCodingSelection` can display `(no coding model assigned) · assigned model` when the model path is empty (`src/PaRuX.App/Views/ModelRoutingPanel.cs:126-132`). Coverage asserts the explicit no-model wording. It does not assert that the summary never implies an assignment, because that expectation currently fails against the product text.
- Setting or resetting a role default refreshes the role route but does not call `RefreshCodingSelectionAsync`, so the effective `/code` summary is not refreshed by those actions (`src/PaRuX.App/Views/ModelRoutingPanel.cs:562-594`). The available deterministic `empty`/`models` fixtures do not provide a registered candidate to assign and then reset. Testing that transition needs a candidate assignment fixture or a product change; this test-only task cannot add either.
