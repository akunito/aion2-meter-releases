using System.Text;
using AionMeter.Service.Overlay;
using Xunit;

namespace AionMeter.Tests;

public class SwaySocketPickTests
{
    [Theory]
    [InlineData("/run/user/1000/sway-ipc.1000.2028160.sock", 2028160)]
    [InlineData("/run/user/1000/sway-ipc.1000.7.sock", 7)]
    [InlineData("/run/user/1000/other.sock", 0)]
    [InlineData("/run/user/1000/sway-ipc.sock", 0)]
    public void Socket_pid_comes_from_the_file_name(string path, int pid) => Assert.Equal(pid, SwayIpc.SocketPid(path));

    [Fact]
    public void Headless_environ_is_recognised_only_on_the_exact_variable()
    {
        static byte[] Env(params string[] vars) => Encoding.UTF8.GetBytes(string.Join('\0', vars) + "\0");
        Assert.True(SwayIpc.IsHeadlessEnviron(Env("HOME=/x", "WLR_BACKENDS=headless,wayland", "SWAYSOCK=/a")));
        Assert.True(SwayIpc.IsHeadlessEnviron(Env("WLR_BACKENDS=headless")));
        Assert.False(SwayIpc.IsHeadlessEnviron(Env("HOME=/x", "XWLR_BACKENDS=headless", "NOTE=WLR_BACKENDS=headless")));
        Assert.False(SwayIpc.IsHeadlessEnviron(Env("WLR_BACKENDS=drm")));
        Assert.False(SwayIpc.IsHeadlessEnviron(ReadOnlySpan<byte>.Empty));
    }
}
