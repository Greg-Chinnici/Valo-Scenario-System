namespace ValoReplay.Knowledge
{
    public enum BuyType : byte { Unknown, Pistol, Eco, Force, Full }

    /// <summary>
    /// Buy prediction from credits (public at round start): approximate gun prices, buy thresholds and the guns each
    /// kind of buy usually brings. Prices are approximate and change with patches; the thresholds are what matter.
    /// </summary>
    public static class Economy
    {
        /// <summary>A rifle and heavy armor.</summary>
        public const int FullBuy = 3900;
        /// <summary>An SMG and light armor.</summary>
        public const int ForceBuy = 2000;
        /// <summary>Operator and heavy armor.</summary>
        public const int OperatorBuy = 5700;

        /// <summary>Approximate price; -1 when unknown (new guns). The simulation's table, which its teams' buys use too.</summary>
        public static int Price(string gun) => ValorantReplay.Sim.WeaponStats.Price(gun);

        /// <summary>Predicted buy for a player's round-start credits.</summary>
        public static BuyType Classify(int credits, bool pistolRound)
        {
            if (credits < 0) return BuyType.Unknown;
            if (pistolRound) return BuyType.Pistol;
            return credits >= FullBuy ? BuyType.Full : credits >= ForceBuy ? BuyType.Force : BuyType.Eco;
        }

        /// <summary>What a bought primary says about the buy (for checking predictions against a recording).</summary>
        public static BuyType ClassifyPrimary(string primary, bool pistolRound)
        {
            if (pistolRound) return BuyType.Pistol;
            if (string.IsNullOrEmpty(primary)) return BuyType.Eco;
            int price = Price(primary);
            return price >= 2900 || price < 0 && primary.Contains("Rifle") ? BuyType.Full : BuyType.Force;
        }

        public static string Name(BuyType b)
        {
            switch (b)
            {
                case BuyType.Pistol: return "PISTOL";
                case BuyType.Eco: return "ECO";
                case BuyType.Force: return "FORCE";
                case BuyType.Full: return "FULL BUY";
                default: return "";
            }
        }

        /// <summary>Guns this buy usually brings, most likely first.</summary>
        public static string LikelyGuns(BuyType b, int credits)
        {
            switch (b)
            {
                case BuyType.Pistol: return "Ghost / Sheriff / Frenzy / Classic";
                case BuyType.Eco: return credits >= 800 ? "Sheriff / Ghost / Shorty" : "Classic / Ghost";
                case BuyType.Force: return "Spectre / Bulldog / Marshal / Judge";
                case BuyType.Full: return credits >= OperatorBuy ? "Vandal / Phantom, Operator possible" : "Vandal / Phantom";
                default: return "";
            }
        }
    }
}
