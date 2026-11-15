// PlanPicker.cs - choosing a plan per round: PlanPicker is a weighted mix (one draw per round from the round's random
// stream), Plans the built-in plans by name for menus and command lines.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace ValorantReplay.Sim
{
    /// <summary>A weighted mix of plan templates, e.g. new PlanPicker&lt;AttackPlan&gt; { { new DefaultAttack(), 3 },
    /// { new FakeAttack(), 1 } }.</summary>
    public sealed class PlanPicker<TPlan> : IEnumerable<(TPlan Plan, float Weight)> where TPlan : class
    {
        readonly List<(TPlan Plan, float Weight)> plans = new List<(TPlan, float)>();

        public int Count => plans.Count;

        public void Add(TPlan plan, float weight)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!(weight >= 0f)) throw new ArgumentException($"weight {weight}: must be 0 or more");
            plans.Add((plan, weight));
        }

        /// <summary>One plan, with chances in proportion to the weights (null when there are none). A picker with a
        /// single plan draws nothing, so it leaves the round's random stream as it was.</summary>
        public TPlan Pick(Random random)
        {
            if (plans.Count == 0) return null;
            if (plans.Count == 1) return plans[0].Plan;
            double total = 0;
            foreach (var p in plans) total += p.Weight;
            double x = random.NextDouble() * total;
            foreach (var p in plans)
                if ((x -= p.Weight) < 0) return p.Plan;
            return plans[plans.Count - 1].Plan;
        }

        public IEnumerator<(TPlan Plan, float Weight)> GetEnumerator() => plans.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The built-in plans. A new plan works without being listed here; add it to pick it by name.</summary>
    public static class Plans
    {
        public static AttackPlan[] Attacks() => new AttackPlan[] { new DefaultAttack(), new RushAttack(), new SlowAttack(), new FakeAttack(), new LateCallAttack() };
        public static DefensePlan[] Defenses() => new DefensePlan[] { new DefaultDefense() };

        /// <summary>A built-in attack plan by Name, ignoring case.</summary>
        public static AttackPlan Attack(string name) => Named(Attacks(), name, p => p.Name);
        public static DefensePlan Defense(string name) => Named(Defenses(), name, p => p.Name);

        /// <summary>A mix from "name=weight,name=weight" (a bare name weighs 1), e.g. "default=3,fake=1,latecall=1".</summary>
        public static PlanPicker<AttackPlan> AttackMix(string spec) => Mix(spec, Attack);
        public static PlanPicker<DefensePlan> DefenseMix(string spec) => Mix(spec, Defense);

        static PlanPicker<T> Mix<T>(string spec, Func<string, T> byName) where T : class
        {
            var picker = new PlanPicker<T>();
            foreach (var part in spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                float w = kv.Length > 1 ? float.Parse(kv[1], CultureInfo.InvariantCulture) : 1f;
                picker.Add(byName(kv[0].Trim()), w);
            }
            return picker;
        }

        static T Named<T>(T[] all, string name, Func<T, string> nameOf)
        {
            foreach (var p in all)
                if (string.Equals(nameOf(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            var names = new List<string>();
            foreach (var p in all) names.Add(nameOf(p).ToLowerInvariant());
            throw new ArgumentException($"no plan named '{name}' (built in: {string.Join(", ", names)})");
        }
    }
}
