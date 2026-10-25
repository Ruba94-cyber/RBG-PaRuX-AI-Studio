using PaRuX.Server.ContextGraph;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// CALLS edges need a call-site identifier (language-agnostic, already extracted for every language) AND a
/// symbol to point at. JavaScript/TypeScript symbol extraction used to recognise only function/class/interface,
/// so class methods, arrow-function consts, enums and type aliases could never be call targets.
/// </summary>
public sealed class RepoIndexJsTsCallTargetTests
{
    private static string[] Names(string path, params string[] lines) =>
        RepoIndexService.ExtractSymbols(path, lines).Select(s => s.Name).ToArray();

    [Fact]
    public void Typescript_class_methods_are_symbols_including_modifiers_generics_and_async()
    {
        string[] names = Names("a.ts",
            "export class Billing {",
            "  version(): number { return 1; }",
            "  public async chargeCard(id: string): Promise<void> {",
            "    if (id) {",
            "      await this.helper(id);",
            "    }",
            "  }",
            "  private static parseInvoice<T>(raw: string): T | null {",
            "  get total(): number {",
            "  PlantedNeedleZq(): string { return 'x'; }",
            "}");
        Assert.Contains("Billing", names);
        Assert.Contains("version", names);
        Assert.Contains("chargeCard", names);
        Assert.Contains("parseInvoice", names);
        Assert.Contains("PlantedNeedleZq", names);
        Assert.DoesNotContain("if", names);
    }

    [Fact]
    public void Arrow_function_consts_enums_and_type_aliases_are_symbols()
    {
        string[] names = Names("a.tsx",
            "export const buildReport = async (items: Item[]) => {",
            "export const handler = function (event) {",
            "const plain = 5;",
            "export enum Mode { A, B }",
            "export type Invoice = { id: string };",
            "export default function main() {}");
        Assert.Contains("buildReport", names);
        Assert.Contains("handler", names);
        Assert.DoesNotContain("plain", names);
        Assert.Contains("Mode", names);
        Assert.Contains("Invoice", names);
        Assert.Contains("main", names);
    }

    [Fact]
    public void Control_flow_and_call_statements_are_never_symbols()
    {
        string[] names = Names("a.js",
            "  if (x) {",
            "  for (const a of b) {",
            "  while (running) {",
            "  switch (kind) {",
            "  } catch (e) {",
            "  foo(bar);",
            "  return baz(1);");
        Assert.Empty(names);
    }

    [Fact]
    public void Call_identifiers_are_extracted_for_ts_go_and_java_call_sites()
    {
        Assert.Contains("chargeCard", RepoIndexService.ExtractCallIdentifiers(new[] { "  await billing.chargeCard(id);" }));
        Assert.Contains("ParseInvoice", RepoIndexService.ExtractCallIdentifiers(new[] { "\tinv, err := billing.ParseInvoice(raw)" }));
        Assert.Contains("computeTotal", RepoIndexService.ExtractCallIdentifiers(new[] { "    int t = invoice.computeTotal(items);" }));
        Assert.Contains("Widget", RepoIndexService.ExtractCallIdentifiers(new[] { "  var w = new Widget<int>(3);" }));
    }

    [Fact]
    public void Go_and_java_methods_and_receivers_are_symbols()
    {
        Assert.Contains("ParseInvoice", Names("a.go", "func (b *Billing) ParseInvoice(raw string) (Invoice, error) {"));
        Assert.Contains("Billing", Names("a.go", "type Billing struct {"));
        Assert.Contains("computeTotal", Names("A.java", "    public static int computeTotal(List<Item> items) throws IOException {"));
    }
}
