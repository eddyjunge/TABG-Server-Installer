using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using TabgInstaller.AdminRadar.Client;
using Xunit;

namespace ClientMods.Tests
{
    public sealed class BotRadarProtocolTests
    {
        /// The layout tabg's server writes (crates/tabg-game/src/room/radar.rs).
        private static byte[] Snapshot(string name, bool trailing = false)
        {
            var b = new List<byte>(Encoding.ASCII.GetBytes("ARDR")) { 2, 3, 1 };
            b.AddRange(new byte[] { 253, 7, 3, 1 | 2 });
            b.AddRange(BitConverter.GetBytes(64.5f));
            foreach (float f in new[] { 1f, 120f, -3f, 10f, 121f, 20f })
                b.AddRange(BitConverter.GetBytes(f));
            b.Add(0);
            b.AddRange(BitConverter.GetBytes(160));
            byte[] n = Encoding.UTF8.GetBytes(name);
            b.AddRange(BitConverter.GetBytes((ushort)n.Length));
            b.AddRange(n);
            if (trailing) b.Add(0);
            return b.ToArray();
        }

        [Fact]
        public void Snapshot_ReadsEveryField()
        {
            RadarProtocol.TryRead(Snapshot("Bot Alva"), out RadarMessage m).Should().BeTrue();
            m.Op.Should().Be(RadarProtocol.Snapshot);
            m.Bots.Should().HaveCount(1);
            Bot bot = m.Bots[0];
            bot.Index.Should().Be(253);
            bot.Group.Should().Be(7);
            bot.State.Should().Be(BotState.Fighting);
            bot.Alive.Should().BeTrue();
            bot.HasGoal.Should().BeTrue();
            bot.Health.Should().Be(64.5f);
            bot.Position.y.Should().Be(120f);
            bot.Goal.z.Should().Be(20f);
            bot.Target.Should().Be(0);
            bot.Gun.Should().Be(160);
            bot.Name.Should().Be("Bot Alva");
        }

        [Fact]
        public void Parser_RejectsTruncatedTrailingAndOtherVersions()
        {
            byte[] good = Snapshot("Bot Alva");
            RadarProtocol.TryRead(good[..^1], out _).Should().BeFalse();
            RadarProtocol.TryRead(Snapshot("Bot Alva", trailing: true), out _).Should().BeFalse();
            byte[] v1 = (byte[])good.Clone();
            v1[4] = 1;
            RadarProtocol.TryRead(v1, out _).Should().BeFalse();
        }

        [Fact]
        public void HelloAndReplies()
        {
            RadarProtocol.CreateHello().Should().Equal(Encoding.ASCII.GetBytes("ARDR").Concat(new byte[] { 2, 1 }));
            RadarProtocol.TryRead(new byte[] { 65, 82, 68, 82, 2, 4, 2 }, out RadarMessage denied).Should().BeTrue();
            denied.Reason.Should().Be(RadarProtocol.DeniedNotAdmin);
            RadarProtocol.TryRead(new byte[] { 65, 82, 68, 82, 2, 2 }, out RadarMessage ok).Should().BeTrue();
            ok.Op.Should().Be(RadarProtocol.Accepted);
        }
    }
}
