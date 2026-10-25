# uit-16 Observability UI coverage gaps

The tests cover the fixed panel headings, fresh-state empty explanations, current numeric text, a refresh busy-to-idle status transition, and the formatted capture time in that status. The current FlaUI launcher does not provide a deterministic way to seed benchmark or timeline records into an isolated app data root. As a result, the UI suite cannot yet verify populated benchmark or timeline timestamps, deliberately non-finite benchmark throughput, or scrolling to the last row of a long list. The refresh test observes the status text transition; it does not assert the progress bar's visual state.

The controls below have no stable `AutomationProperties.AutomationId` in the Observability page. Tests find static elements by `ControlType` plus source-verified `Name` or text. For response-backed numeric values, the test enumerates `ControlType.Text` descendants and inspects each current `Name`. Suggested IDs:

| Control | Page | Suggested ID |
| --- | --- | --- |
| Refresh button (`Button`, name `Refresh`) | Observability | `Observability_Refresh_Button` |
| Busy progress bar (`ProgressBar`) | Observability | `Observability_Busy_ProgressBar` |
| Refresh status text block | Observability | `Observability_Status_Text` |
| Page title and three panel headings | Observability | `Observability_Title_Text`, `Observability_Snapshot_Heading`, `Observability_Benchmarks_Heading`, `Observability_Timeline_Heading` |
| Benchmark and timeline empty-state text | Observability | `Observability_Benchmark_EmptyState_Text`, `Observability_Timeline_EmptyState_Text` |
| Metric label, value, and detail text blocks | Observability | `Observability_Metric_{metricId}_Label`, `Observability_Metric_{metricId}_Value`, `Observability_Metric_{metricId}_Detail` |
| Benchmark row timestamp and throughput text | Observability | `Observability_Benchmark_{rank}_Timestamp`, `Observability_Benchmark_{rank}_Throughput` |
| Timeline summary and entry timestamp text | Observability | `Observability_Timeline_Summary_Text`, `Observability_Timeline_{entryId}_Timestamp` |
