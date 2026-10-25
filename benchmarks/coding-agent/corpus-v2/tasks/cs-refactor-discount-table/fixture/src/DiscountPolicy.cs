public static class DiscountPolicy
{
    public static int Percent(string customerTier)
    {
        if (customerTier == null) return 0;
        string t = customerTier.ToLowerInvariant();
        if (t == "bronze") return 2;
        else if (t == "silver") return 5;
        else if (t == "gold") return 10;
        else if (t == "platinum") return 15;
        else if (t == "staff") return 30;
        else return 0;
    }
}
