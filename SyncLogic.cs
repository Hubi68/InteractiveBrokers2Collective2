namespace IBCollective2Sync;

public static class SyncLogic
{
    public static (string action, double quantity)? DetermineAction(
        double ibQuantity, double c2Quantity, double minimumThreshold)
    {
        var quantityDiff = ibQuantity - c2Quantity;

        if (Math.Abs(ibQuantity) < minimumThreshold)
        {
            if (Math.Abs(c2Quantity) > minimumThreshold)
            {
                string action = c2Quantity < 0 ? "BTC" : "STC";
                return (action, Math.Abs(c2Quantity));
            }
            return null;
        }
        else if (quantityDiff > 0)
        {
            string action = c2Quantity < 0 ? "BTC" : "BTO";
            return (action, Math.Abs(quantityDiff));
        }
        else
        {
            string action = c2Quantity > 0 ? "STC" : "STO";
            return (action, Math.Abs(quantityDiff));
        }
    }
}
