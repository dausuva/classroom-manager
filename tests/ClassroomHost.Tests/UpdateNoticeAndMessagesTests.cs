// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Net.Http;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public class UpdateNoticeTests
{
    const string Release = """
        { "name": "Classroom Manager 1.2.0 (build 310)", "tag_name": "2026-10-20-abc1234", "draft": false, "prerelease": false,
          "html_url": "https://github.com/dausuva/classroom-manager/releases/tag/2026-10-20-abc1234" }
        """;

    [Fact]
    public void A_release_is_read_from_its_title()
    {
        var release = UpdateNotice.Parse(Release);

        Assert.Equal(("1.2.0.310", "https://github.com/dausuva/classroom-manager/releases/tag/2026-10-20-abc1234"), (release!.Version, release.Page));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"name\": \"Some other thing\", \"html_url\": \"https://github.com/x/y\" }")]
    [InlineData("{ \"name\": \"Classroom Manager 1.2.0 (build 310)\", \"html_url\": \"https://evil.example/download\" }")]   // only the project's own site
    [InlineData("{ \"name\": \"Classroom Manager 1.2.0 (build 310)\", \"draft\": true, \"html_url\": \"https://github.com/x/y\" }")]
    [InlineData("{ \"name\": \"Classroom Manager 1.2.0 (build 310)\", \"prerelease\": true, \"html_url\": \"https://github.com/x/y\" }")]
    [InlineData("{ \"message\": \"Not Found\" }")]
    public void Anything_else_is_not_a_release(string json)
    {
        Assert.Null(UpdateNotice.Parse(json));
    }

    [Theory]
    [InlineData("1.2.0.303", "1.2.0.310", true)]
    [InlineData("1.2.0.303", "1.3.0.1", true)]
    [InlineData("1.2.0.310", "1.2.0.310", false)]
    [InlineData("1.2.0.311", "1.2.0.310", false)]
    [InlineData("Unknown", "1.2.0.310", false)]
    [InlineData("1.2.0.303", "", false)]
    [InlineData("1.2.0", "1.2.0.310", false)]    // a customer release (three numbers) is not behind the internal build of its own version
    [InlineData("1.2.0", "1.2.1.5", true)]
    [InlineData("1.2.0", "1.3.0.1", true)]
    [InlineData("1.2.1", "1.2.0.900", false)]
    public void Only_a_later_version_is_announced(string running, string offered, bool newer)
    {
        Assert.Equal(newer, UpdateNotice.IsNewer(running, offered));
    }

    [Fact]
    public void The_look_happens_once_a_day()
    {
        var now = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

        Assert.True(UpdateNotice.Due("", now));
        Assert.True(UpdateNotice.Due("yesterday", now));
        Assert.True(UpdateNotice.Due(now.AddHours(-25).ToString("O"), now));
        Assert.False(UpdateNotice.Due(now.AddHours(-3).ToString("O"), now));
        Assert.True(UpdateNotice.Due(now.AddDays(3).ToString("O"), now));     // a clock that was wrong must not silence it for ever
    }

    sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }

    [Fact]
    public async Task One_request_is_made_and_no_network_means_no_notice()
    {
        var handler = new Answer(HttpStatusCode.OK, Release);
        using (var http = new HttpClient(handler))
        {
            Assert.Equal("1.2.0.310", (await UpdateNotice.CheckAsync(http, "1.2.0.303", CancellationToken.None))?.Version);
            Assert.Equal(UpdateNotice.LatestReleaseUrl, handler.Asked!.RequestUri!.ToString());
            Assert.Null(await UpdateNotice.CheckAsync(http, "1.2.0.310", CancellationToken.None));
        }
        using (var http = new HttpClient(new Answer(HttpStatusCode.NotFound, "{ \"message\": \"Not Found\" }")))
            Assert.Null(await UpdateNotice.CheckAsync(http, "1.2.0.303", CancellationToken.None));
        using (var http = new HttpClient(new NoNetwork()))
            Assert.Null(await UpdateNotice.CheckAsync(http, "1.2.0.303", CancellationToken.None));
    }
}

public sealed class AdminMessagesTests : IDisposable
{
    readonly string _folder = Path.Combine(Directory.CreateTempSubdirectory("cm-messages-").FullName, "HostMessages");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);

    [Fact]
    public void A_note_waits_until_it_is_removed()
    {
        var messages = new AdminMessages(_folder);
        Assert.Empty(messages.Waiting());

        Assert.Equal("", messages.Leave("PC-07 has no sound.", "teacher1", "1.2.0.303", new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("", messages.Leave("  Please unblock example.org  ", "teacher2", "1.2.0.303", new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc)));

        // Another Windows account, later: the same folder, oldest first.
        var waiting = new AdminMessages(_folder).Waiting();
        Assert.Equal(["PC-07 has no sound.", "Please unblock example.org"], waiting.Select(message => message.Text));
        Assert.Equal("teacher1", waiting[0].From);
        Assert.Contains("teacher1", waiting[0].Heading);

        Assert.Equal(1, messages.Remove([waiting[0]]));
        Assert.Equal("Please unblock example.org", Assert.Single(messages.Waiting()).Text);
        Assert.Equal(0, messages.Remove([waiting[0]]));                         // already gone
    }

    [Fact]
    public void An_empty_or_endless_note_is_refused_and_nothing_outside_the_folder_is_touched()
    {
        var messages = new AdminMessages(_folder);

        Assert.NotEqual("", messages.Leave("   ", "teacher", "1.2.0.303"));
        Assert.NotEqual("", messages.Leave(new string('x', AdminMessages.MaxLength + 1), "teacher", "1.2.0.303"));
        Assert.Empty(messages.Waiting());

        var outside = Path.Combine(Path.GetDirectoryName(_folder)!, "keep.json");
        File.WriteAllText(outside, "{}");
        Assert.Equal(0, messages.Remove([new AdminMessage { Id = "..\\keep" }, new AdminMessage { Id = "" }]));
        Assert.True(File.Exists(outside));

        // A damaged file in the folder does not hide the others.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "not json");
        Assert.Equal("", messages.Leave("Still readable", "teacher", "1.2.0.303"));
        Assert.Equal("Still readable", Assert.Single(messages.Waiting()).Text);
    }
}
