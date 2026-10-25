export function shippingCost(region: string, weightKg: number): number {
  switch (region) {
    case "eu":
      return 5 + 1.5 * weightKg;
    case "us":
      return 8 + 2 * weightKg;
    case "uk":
      return 6 + 1.75 * weightKg;
    case "apac":
      return 12 + 3 * weightKg;
    case "latam":
      return 14 + 3.5 * weightKg;
    default:
      throw new Error("unknown region: " + region);
  }
}
