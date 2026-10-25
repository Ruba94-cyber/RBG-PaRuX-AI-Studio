const RATES = new Map<string, { base: number; perKg: number }>([
  ["eu", { base: 5, perKg: 1.5 }],
  ["us", { base: 8, perKg: 2 }],
  ["uk", { base: 6, perKg: 1.75 }],
  ["apac", { base: 12, perKg: 3 }],
  ["latam", { base: 14, perKg: 3.5 }],
]);

export function shippingCost(region: string, weightKg: number): number {
  const rate = RATES.get(region);
  if (!rate) {
    throw new Error("unknown region: " + region);
  }
  return rate.base + rate.perKg * weightKg;
}
