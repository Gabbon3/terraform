using terraform.lib.engine.actions;

namespace terraform.Tests;

public sealed class HostsFileTests
{
    private static HostEntry Entry(string ip, string host) => new(ip, host);

    private static HostsEdit Apply(string text, params HostEntry[] entries) => HostsFile.Apply(text, entries);

    [Fact]
    public void Adds_a_missing_host_keeping_existing_content_and_crlf()
    {
        var text = "# header\r\n127.0.0.1\tlocalhost\r\n\r\n";

        var edit = Apply(text, Entry("10.0.0.5", "server1"));

        Assert.Equal("# header\r\n127.0.0.1\tlocalhost\r\n\r\n10.0.0.5\tserver1\r\n", edit.Text);
        Assert.Equal(new[] { "ADD 10.0.0.5\tserver1" }, edit.Changes);
    }

    [Fact]
    public void An_aligned_file_is_returned_unchanged_even_with_odd_formatting()
    {
        var text = "10.0.0.5     SERVER1   # mio\r\n";

        var edit = Apply(text, Entry("10.0.0.5", "server1"));

        Assert.Empty(edit.Changes);
        Assert.Equal(text, edit.Text);
    }

    [Fact]
    public void A_host_with_another_ip_is_moved_and_its_old_single_host_line_disappears()
    {
        var edit = Apply("1.1.1.1\tserver1\r\n2.2.2.2\tother\r\n", Entry("10.0.0.5", "server1"));

        Assert.Equal("2.2.2.2\tother\r\n10.0.0.5\tserver1\r\n", edit.Text);
        Assert.Equal(new[] { "UPDATE server1: 1.1.1.1 -> 10.0.0.5" }, edit.Changes);
    }

    [Fact]
    public void Matches_any_alias_on_a_line_and_keeps_the_other_aliases_and_the_comment()
    {
        var edit = Apply("1.1.1.1 alpha server1 beta # note\r\n", Entry("10.0.0.5", "SERVER1"));

        Assert.Equal("1.1.1.1\talpha beta # note\r\n10.0.0.5\tSERVER1\r\n", edit.Text);
    }

    [Fact]
    public void Removes_duplicate_lines_keeping_the_first_exact_one()
    {
        var edit = Apply("10.0.0.5 server1\r\n10.0.0.5 server1\r\n9.9.9.9 server1\r\n", Entry("10.0.0.5", "server1"));

        Assert.Equal("10.0.0.5 server1\r\n", edit.Text);
        Assert.Single(edit.Changes);
    }

    [Fact]
    public void Commented_out_mappings_and_blank_lines_are_never_touched()
    {
        var text = "# 1.1.1.1 server1\r\n\r\n   # 2.2.2.2 server1\r\n";

        var edit = Apply(text, Entry("10.0.0.5", "server1"));

        Assert.Equal(text + "10.0.0.5\tserver1\r\n", edit.Text);
    }

    [Fact]
    public void Keeps_lf_line_endings_and_a_missing_final_newline()
    {
        var edit = Apply("127.0.0.1 localhost", Entry("10.0.0.5", "server1"));

        Assert.Equal("127.0.0.1 localhost\r\n10.0.0.5\tserver1", edit.Text);

        var lf = Apply("127.0.0.1 localhost\n", Entry("10.0.0.5", "server1"));

        Assert.Equal("127.0.0.1 localhost\n10.0.0.5\tserver1\n", lf.Text);
    }

    [Fact]
    public void An_empty_file_gets_the_entry_and_a_final_newline()
    {
        Assert.Equal("10.0.0.5\tserver1\r\n", Apply("", Entry("10.0.0.5", "server1")).Text);
    }

    [Fact]
    public void Different_spellings_of_the_same_ipv6_address_are_equal()
    {
        var edit = Apply("0:0:0:0:0:0:0:1 localhost\r\n", Entry("::1", "localhost"));

        Assert.Empty(edit.Changes);
    }

    [Fact]
    public void Several_entries_are_applied_in_order()
    {
        var edit = Apply("", Entry("10.0.0.1", "a"), Entry("10.0.0.2", "b"));

        Assert.Equal("10.0.0.1\ta\r\n10.0.0.2\tb\r\n", edit.Text);
        Assert.Equal(2, edit.Changes.Count);
    }
}
