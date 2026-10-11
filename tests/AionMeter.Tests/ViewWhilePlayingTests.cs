using AionMeter.Core.Character;
using AionMeter.Core.Events;
using AionMeter.Core.Inventory;

namespace AionMeter.Tests;

/// <summary>A2M-28 view-while-playing: who is bound / viewed after logins and picks, and the read-only file store behind another character's tabs.</summary>
public class ViewWhilePlayingTests
{
    [Fact]
    public void Login_binds_and_views_the_character_and_a_repeat_identity_leaves_the_view_alone()
    {
        var sel = new CharacterSelection();
        Assert.True(sel.Login("2001-Akunita"));
        Assert.Equal(("2001-Akunita", "2001-Akunita", false), (sel.Bound, sel.Viewed, sel.IsViewingOther));
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Irin", gameRunning: true));
        Assert.False(sel.Login("2001-Akunita"));                 // identity packets repeat while playing
        Assert.Equal(("2001-Akunita", "2001-Irin", true), (sel.Bound, sel.Viewed, sel.IsViewingOther));
        Assert.False(sel.Login(""));
    }

    [Fact]
    public void Pick_while_playing_moves_only_the_view_and_back_to_the_bound_one_clears_it()
    {
        var sel = new CharacterSelection();
        sel.Login("2001-Akunita");
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Irin", gameRunning: true));
        Assert.Equal("2001-Akunita", sel.Bound);
        Assert.Equal(CharacterPick.None, sel.Pick("2001-Irin", gameRunning: true));
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Akunita", gameRunning: true));
        Assert.False(sel.IsViewingOther);
    }

    [Fact]
    public void Switching_character_in_game_pulls_the_view_to_the_new_one_even_from_a_third()
    {
        var sel = new CharacterSelection();
        sel.Login("2001-Akunita");
        sel.Pick("2001-Arielka", gameRunning: true);
        Assert.True(sel.Login("2001-Irin"));                    // logout Akunita, login Irin while Arielka was viewed
        Assert.Equal(("2001-Irin", "2001-Irin", false), (sel.Bound, sel.Viewed, sel.IsViewingOther));
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Akunita", gameRunning: true));   // still free to view anyone after
        Assert.Equal("2001-Irin", sel.Bound);
    }

    [Fact]
    public void Pick_with_the_game_closed_rebinds_except_back_to_the_bound_one_and_the_next_login_rebinds_with_mirrors()
    {
        var sel = new CharacterSelection();
        sel.Login("2001-Akunita");                              // last session
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Irin", gameRunning: true));
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Akunita", gameRunning: false));   // back to the bound one: nothing to rebind
        Assert.False(sel.BoundByHand);
        Assert.Equal(CharacterPick.Rebind, sel.Pick("2001-Irin", gameRunning: false));
        Assert.Equal(("2001-Irin", "2001-Irin", true), (sel.Bound, sel.Viewed, sel.BoundByHand));
        Assert.True(sel.Login("2001-Irin"));                    // the game logs the same character in: rebind with the legacy mirrors
        Assert.False(sel.BoundByHand);
        Assert.False(sel.Login("2001-Irin"));
    }

    [Fact]
    public void Pick_before_any_identity_views_without_binding_while_playing()
    {
        var sel = new CharacterSelection();
        Assert.Equal(CharacterPick.View, sel.Pick("2001-Irin", gameRunning: true));
        Assert.Equal(("", "2001-Irin", true), (sel.Bound, sel.Viewed, sel.IsViewingOther));
        Assert.True(sel.Login("2001-Akunita"));
        Assert.Equal("2001-Akunita", sel.Viewed);
    }

    [Fact]
    public void Read_only_store_follows_the_file_at_most_every_5_s_and_never_writes()
    {
        var root = Path.Combine(Path.GetTempPath(), "view-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "inventory.json");
        try
        {
            var writer = new InventoryStore(path);
            writer.OnEvent(new InventoryEvent(1_000, false, new[] { new ItemStack(7, InventoryStore.Kinah, 100, Containers.Bag, 0, 0) }));
            writer.Save();
            var t0 = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, t0);

            var view = InventoryStore.ReadOnly(path);
            Assert.True(view.IsReadOnly);
            Assert.Equal(100, view.CountOf(InventoryStore.Kinah));
            Assert.Equal(1_000, view.Seen(Containers.Bag).SeenMs);   // "last seen" survives the file round trip
            var changed = 0; view.Changed += () => changed++;

            // packets and saves never touch a read-only store or its file
            view.OnEvent(new InventoryEvent(2_000, false, new[] { new ItemStack(7, InventoryStore.Kinah, 999, Containers.Bag, 0, 0) }));
            view.Save();
            Assert.Equal(100, view.CountOf(InventoryStore.Kinah));
            Assert.Equal(t0, File.GetLastWriteTimeUtc(path));

            Assert.False(view.ReloadIfChanged(10_000));              // first check: file unchanged
            writer.OnEvent(new InventoryEvent(3_000, false, new[] { new ItemStack(7, InventoryStore.Kinah, 250, Containers.Bag, 0, 0) }));
            writer.Save();
            File.SetLastWriteTimeUtc(path, t0.AddSeconds(30));
            Assert.False(view.ReloadIfChanged(12_000));              // < 5 s since the last check
            Assert.Equal(100, view.CountOf(InventoryStore.Kinah));
            Assert.True(view.ReloadIfChanged(15_000));
            Assert.Equal(250, view.CountOf(InventoryStore.Kinah));
            Assert.Equal(1, changed);
            Assert.False(view.ReloadIfChanged(20_000));              // same mtime: no reload, no event
            Assert.Equal(1, changed);

            // rebinding a read-only store loads the other file and writes nothing
            var other = Path.Combine(root, "b", "inventory.json");
            view.Bind(other, Path.Combine(root, "mirror.json"));
            Assert.Equal(0, view.CountOf(InventoryStore.Kinah));
            Assert.False(File.Exists(other));
            Assert.False(File.Exists(Path.Combine(root, "mirror.json")));
            Assert.Equal(t0.AddSeconds(30), File.GetLastWriteTimeUtc(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
