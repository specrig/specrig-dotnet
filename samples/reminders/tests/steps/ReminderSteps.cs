using Reqnroll;
using Reminders.Specs.Internal;
using Specrig;

namespace Reminders.Specs.Steps;

[Binding]
public sealed class ReminderSteps(Harness harness)
{
    IReadOnlyList<Settled>? settled;

    [Given("billing holds an unpaid invoice of {int} for order {int}, due {word}")]
    public void Unpaid(int amount, int order, string when)
    {
        var dueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(when switch
        {
            "yesterday" => -1,
            "tomorrow" => 1,
            _ => throw new ArgumentException($"due {when}: yesterday or tomorrow"),
        });
        harness.Billing.ReplyTo("GET", "/invoices", new() { ["status"] = "unpaid" }, 200, new[] { new { order, amount, dueOn } });
    }

    [Given("billing holds unpaid invoices of {int} for order {int} and {int} for order {int}, both due yesterday")]
    public void BothUnpaid(int amount, int order, int other, int otherOrder)
    {
        var dueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        harness.Billing.ReplyTo("GET", "/invoices", new() { ["status"] = "unpaid" }, 200,
            new[] { new { order, amount, dueOn }, new { order = otherOrder, amount = other, dueOn } });
    }

    [When("the scheduler fires the reminders")]
    public Task Fire() => harness.Run<RemindersJob>();

    [Then("billing is asked to remind one customer")]
    public void One() => Assert.Single(harness.Billing.Requests("POST", "/reminders"));

    [Given("billing accepts reminders")]
    public void Accepts() => harness.Billing.Reply("POST", "/reminders", 202);

    [Given("billing has reported order {int} paid")]
    public async Task Reported(int order) => Assert.Equal([Settled.Acked], await harness.Bus.Deliver("invoices.paid", new { order }));

    [When("the reminders run")]
    public Task Run() => harness.Run<SendReminders>();

    [When("someone runs the reminders by hand")]
    public async Task ByHand() => Assert.Equal(0, await harness.Command("remind"));

    [When("billing reports order {int} paid")]
    public async Task Reports(int order) => settled = await harness.Bus.Deliver("invoices.paid", new { order });

    [When("billing reports a payment nobody can read")]
    public async Task Unreadable() => settled = await harness.Bus.Deliver("invoices.paid", "a payment, in prose");

    [Then("billing is asked to remind the customer of {int} for order {int}")]
    public void Reminded(int amount, int order) =>
        Assert.Equal(new Reminder(order, amount), Assert.Single(harness.Billing.Requests("POST", "/reminders")).Body<Reminder>());

    [Then("billing is asked to remind nobody")]
    public void Nobody() => Assert.Empty(harness.Billing.Requests("POST", "/reminders"));

    [Then("the reminder of {int} for order {int} is announced")]
    public void Announced(int amount, int order) =>
        Assert.Equal(new Reminder(order, amount), Assert.Single(harness.Bus.Published("reminders.sent")).Body<Reminder>());

    [Then("the report is acknowledged")]
    public void Acknowledged() => Assert.Equal([Settled.Acked], settled);

    [Then("the report is dropped")]
    public void Dropped() => Assert.Equal([Settled.Dropped], settled);
}
