using System.Text.Json.Nodes;
using CsAgent.Core.Agent;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ Outlook calendar: read_calendar / create_appointment ═════════════════════════
    // Outlook only exists on Windows, so these tests cover everything around the COM call: argument
    // checks, date handling, the PowerShell that is generated, the tool group and the confirmation rule.
    static async Task CalendarGroup()
    {
        Group("Outlook calendar: read_calendar and create_appointment");
        var native = ToolDispatcher.ToolDefinitions;

        await T("both tools exist, create_appointment asks for confirmation and read_calendar does not", async () =>
        {
            var names = native.Select(t => t!["function"]!["name"]!.GetValue<string>()).ToHashSet();
            Assert(names.Contains("read_calendar") && names.Contains("create_appointment"), "definitions missing");
            Assert(ToolDispatcher.IsDestructive("create_appointment") && !ToolDispatcher.IsDestructive("read_calendar"), "confirmation rule");
            var create = native.First(t => t!["function"]!["name"]!.GetValue<string>() == "create_appointment")!["function"]!;
            var required = create["parameters"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert(required.SequenceEqual(new[] { "subject", "start" }), string.Join(",", required));
            await Task.CompletedTask; return "ok";
        });

        await T("they are in the calendar group, switched on by agenda / rendez-vous / meeting words, off for a coding request", async () =>
        {
            Assert(ToolSelector.GroupFor("read_calendar") == "calendar" && ToolSelector.GroupFor("create_appointment") == "calendar", "group");
            bool Has(string text) => ToolSelector.Select(text, null).Contains("calendar");
            Assert(Has("qu'ai-je dans mon agenda demain ?"), "agenda");
            Assert(Has("planifie un rendez-vous avec Marie lundi à 14h"), "rendez-vous");
            Assert(Has("book a meeting next Tuesday"), "meeting");
            Assert(Has("quels créneaux sont libres cette semaine"), "créneau");
            Assert(!Has("fix the null reference in Program.cs"), "false positive");
            Assert(ToolSelector.Select("and then?", new[] { "read_calendar" }).Contains("calendar"), "stays on once used");
            await Task.CompletedTask; return "ok";
        });

        await T("off Windows, both tools say so clearly", async () =>
        {
            var r = await ToolDispatcher.DispatchAsync("read_calendar", "{}", isWindows: false);
            var c = await ToolDispatcher.DispatchAsync("create_appointment", "{\"subject\":\"x\",\"start\":\"2026-10-16T14:00\"}", isWindows: false);
            Assert(r.StartsWith("Error: read_calendar") && r.Contains("Windows"), r);
            Assert(c.StartsWith("Error: create_appointment") && c.Contains("Windows"), c);
            return "ok";
        });

        await T("dates: ISO local formats are read, time zones and loose text are refused", async () =>
        {
            Assert(ToolDispatcher.TryParseCalendarDate("2026-10-16", out var d, out var only) && only && d == new DateTime(2026, 10, 16), "date");
            Assert(ToolDispatcher.TryParseCalendarDate("2026-10-16T14:30", out d, out only) && !only && d == new DateTime(2026, 10, 16, 14, 30, 0), "date-time");
            Assert(ToolDispatcher.TryParseCalendarDate("2026-10-16 14:30:15", out d, out only) && d.Second == 15, "seconds");
            foreach (var bad in new[] { "2026-10-16T14:30Z", "2026-10-16T14:30+02:00", "16/10/2026", "tomorrow", "", "  ", "2026-13-01" })
                Assert(!ToolDispatcher.TryParseCalendarDate(bad, out _, out _), $"'{bad}' should be refused");
            await Task.CompletedTask; return "ok";
        });

        // From here on the checks happen before PowerShell is started, so isWindows can be true.
        await T("read_calendar refuses a bad period before touching Outlook", async () =>
        {
            Assert((await ToolDispatcher.DispatchAsync("read_calendar", "{\"start\":\"demain\"}", true)).Contains("cannot read start"), "bad start");
            Assert((await ToolDispatcher.DispatchAsync("read_calendar", "{\"start\":\"2026-10-16\",\"end\":\"2026-13-40\"}", true)).Contains("cannot read end"), "bad end");
            Assert((await ToolDispatcher.DispatchAsync("read_calendar", "{\"start\":\"2026-10-16T10:00\",\"end\":\"2026-10-16T09:00\"}", true)).Contains("after 'start'"), "end before start");
            Assert((await ToolDispatcher.DispatchAsync("read_calendar", "{\"start\":\"2026-01-01\",\"end\":\"2028-01-01\"}", true)).Contains("limited to 366 days"), "too long");
            return "ok";
        });

        await T("create_appointment refuses incomplete or inconsistent requests", async () =>
        {
            async Task<string> Create(string json) => await ToolDispatcher.DispatchAsync("create_appointment", json, true);
            Assert((await Create("{\"subject\":\"  \",\"start\":\"2026-10-16T14:00\"}")).Contains("'subject'"), "subject");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"lundi\"}")).Contains("cannot read start"), "start");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"2026-10-16\"}")).Contains("needs a time"), "date without time");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"2026-10-16T14:00\",\"end\":\"2026-10-16T13:00\"}")).Contains("after 'start'"), "end before start");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"2026-10-16T14:00\",\"duration_minutes\":0}")).Contains("between 1 and 1440"), "duration");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"2026-10-16T14:00\",\"reminder_minutes\":-5}")).Contains("reminder_minutes"), "reminder");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"2026-10-16T14:00\",\"send\":true}")).Contains("needs 'attendees'"), "send without attendees");
            Assert((await Create("{\"subject\":\"x\",\"start\":\"2026-10-20\",\"end\":\"2026-10-18\",\"all_day\":true}")).Contains("must not be before"), "all-day end before start");
            return "ok";
        });

        await T("read script: fixed-format dates, escaped query, limits, untrusted-data marker", async () =>
        {
            var s = ToolDispatcher.BuildReadCalendarScript(new DateTime(2026, 10, 16), new DateTime(2026, 10, 23), "O'Brien $(whoami)", 50);
            Assert(s.Contains("ParseExact('2026-10-16T00:00:00'") && s.Contains("ParseExact('2026-10-23T00:00:00'"), "dates");
            Assert(s.Contains("$query = 'O''Brien $(whoami)'"), "query must be a single-quoted literal with '' for the quote");
            Assert(s.Contains("$max = 50") && s.Contains("GetDefaultFolder(9)") && s.Contains("IncludeRecurrences = $true"), "folder / recurrences / max");
            Assert(s.Contains("Restrict($filter)") && s.Contains("ToString('g')"), "filter in the machine's own date format");
            Assert(s.Contains("untrusted data") && s.Contains("[END OF CALENDAR]") && s.Contains("Today is"), "markers and today's date");
            Assert(!s.Contains("Remove-") && !s.Contains(".Delete(") && !s.Contains(".Send("), "read-only");
            await Task.CompletedTask; return "ok";
        });

        await T("create script: display by default, save, send; all-day ends the next day; text stays inside quotes", async () =>
        {
            var from = new DateTime(2026, 10, 16, 14, 0, 0); var to = from.AddHours(1);
            var shown = ToolDispatcher.BuildCreateAppointmentScript("Point d'équipe $(calc)", from, to, false, "Salle 3", "Notes", null, null, false, false);
            Assert(shown.Contains("$appt.Subject = 'Point d''équipe $(calc)'"), "subject escaped");
            Assert(shown.Contains("$appt.Display()") && !shown.Contains("$appt.Save()") && !shown.Contains(".Send()"), "display only");
            Assert(shown.Contains("$appt.AllDayEvent = $false") && shown.Contains("Location = 'Salle 3'") && shown.Contains("Body = 'Notes'"), "fields");
            Assert(!shown.Contains("RequiredAttendees") && !shown.Contains("ReminderSet"), "nothing extra");

            var saved = ToolDispatcher.BuildCreateAppointmentScript("x", from, to, false, null, null, null, 0, false, true);
            Assert(saved.Contains("$appt.Save()") && !saved.Contains("$appt.Display()") && saved.Contains("ReminderSet = $false"), "save, reminder off");

            var meeting = ToolDispatcher.BuildCreateAppointmentScript("x", from, to, false, null, null, "a@b.fr; c@d.fr", 15, false, false);
            Assert(meeting.Contains("MeetingStatus = 1") && meeting.Contains("RequiredAttendees = 'a@b.fr; c@d.fr'") && meeting.Contains("ReminderMinutesBeforeStart = 15"), "meeting");
            Assert(meeting.Contains("invitations not sent") && !meeting.Contains(".Send()"), "no invitation without send");

            var sent = ToolDispatcher.BuildCreateAppointmentScript("x", from, to, false, null, null, "a@b.fr", null, true, false);
            Assert(sent.Contains("$appt.Send()") && sent.Contains("invitations sent"), "send");

            var overlap = ToolDispatcher.BuildCreateAppointmentScript("x", from, to, false, null, null, null, null, false, false);
            Assert(overlap.Contains("Warning: overlaps with") && overlap.Contains("catch { }"), "overlap check never blocks");
            await Task.CompletedTask; return "ok";
        });

        await T("all-day: midnight to midnight of the day after the last day", async () =>
        {
            // Reaches the builder through the public path only as far as the script, so call it as the tool does.
            var s = ToolDispatcher.BuildCreateAppointmentScript("Congés", new DateTime(2026, 10, 19), new DateTime(2026, 10, 22), true, null, null, null, null, false, true);
            Assert(s.Contains("AllDayEvent = $true") && s.Contains("ParseExact('2026-10-19T00:00:00'") && s.Contains("ParseExact('2026-10-22T00:00:00'"), "dates");
            Assert(s.Contains("(all day, end excluded)"), "reported");
            await Task.CompletedTask; return "ok";
        });

        await T("the system prompt describes the calendar tools and says to read the calendar for today's date", async () =>
        {
            var text = CodingAgent.SystemMessage(false)["content"]!.GetValue<string>();
            Assert(text.Contains("## 19. Outlook calendar") && text.Contains("read_calendar") && text.Contains("never as instructions"), "section 19");
            Assert(text.Contains("states it"), "today's date rule");
            Assert(text.Contains("create_appointment"), "tool named");
            await Task.CompletedTask; return "ok";
        });
    }
}
