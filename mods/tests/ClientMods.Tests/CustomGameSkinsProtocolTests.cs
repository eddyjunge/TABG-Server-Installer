using System;
using FluentAssertions;
using Xunit;

namespace TabgInstaller.CustomGameSkins.Tests
{
    public sealed class CustomGameSkinsProtocolTests
    {
        [Fact]
        public void ApplyOutfit_RoundTripsAllSixGearSlots()
        {
            var outfit = new[] { 10, -1, 20, 3, 30, 4, 40, 5, 50, 6, 60, 7 };

            var payload = CustomGameSkinsProtocol.CreateApplyOutfit(outfit);
            var parsed = CustomGameSkinsProtocol.TryRead(payload, out var operation, out var parsedOutfit, out var reason);

            parsed.Should().BeTrue();
            operation.Should().Be(CustomGameSkinsProtocol.ApplyOutfit);
            parsedOutfit.Should().Equal(outfit);
            reason.Should().Be(0);
        }

        [Fact]
        public void Parser_RejectsTruncatedAndTrailingOutfitPayloads()
        {
            var payload = CustomGameSkinsProtocol.CreateApplyOutfit(
                new[] { 10, -1, 20, -1, 30, -1, 40, -1, 50, -1, 60, -1 });
            var truncated = payload[..^1];
            var trailing = new byte[payload.Length + 1];
            Array.Copy(payload, trailing, payload.Length);

            CustomGameSkinsProtocol.TryRead(truncated, out _, out _, out _).Should().BeFalse();
            CustomGameSkinsProtocol.TryRead(trailing, out _, out _, out _).Should().BeFalse();
        }

        [Fact]
        public void CreateApplyOutfit_RequiresExactlyTwelveValues()
        {
            Action act = () => CustomGameSkinsProtocol.CreateApplyOutfit(new int[10]);

            act.Should().Throw<ArgumentException>();
        }
    }
}
