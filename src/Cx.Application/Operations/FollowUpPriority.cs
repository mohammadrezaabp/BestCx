namespace Cx.Application.Operations;

public static class FollowUpPriority
{
    public static void Apply(FollowUpCustomer customer)
    {
        var score = 0;
        var reasons = new List<string>();
        if (customer.Complaints > 0) { score += (int)Math.Min(32, customer.Complaints * 8); reasons.Add($"{customer.Complaints} Complaint within 30 days"); }
        if (customer.OpenCases > 0) { score += (int)Math.Min(28, customer.OpenCases * 7); reasons.Add($"{customer.OpenCases} Unsolved case"); }
        if (customer.OverdueCases > 0) { score += (int)Math.Min(40, customer.OverdueCases * 20); reasons.Add($"{customer.OverdueCases} The case is out of time"); }
        customer.FollowUpScore = Math.Min(100, score);
        customer.Reasons = reasons.ToArray();
    }
}
