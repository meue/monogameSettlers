using Homestead.Simulation.Economy;
using Homestead.Simulation.Map;

namespace Homestead.Simulation.Tests;

/// <summary>The shares of Siedler › Berufe: together they may never exceed 100 % (a carrier floor of
/// 100 % would brick specialist recruitment), single shares can still take the whole.</summary>
public class RecruitmentShareTests
{
    /// <summary>The real balancing, so the shares' defaults are the player-facing 30/25/25 %.</summary>
    private readonly World world = new(new GameMap(70), ProductionConfig.Load());

    [Fact]
    public void Diggers_set_above_the_remaining_budget_clamp_to_the_remainder()
    {
        world.Recruitment.SetShare(0, Share.Carriers, 0.30);
        world.Recruitment.SetShare(0, Share.Builders, 0.25);
        world.Recruitment.SetShare(0, Share.Diggers, 1.0);
        Assert.Equal(0.45, world.Recruitment.ShareOf(0, Share.Diggers));
    }

    [Fact]
    public void Carriers_can_take_the_whole_when_the_others_stand_at_zero()
    {
        world.Recruitment.SetShare(0, Share.Builders, 0);
        world.Recruitment.SetShare(0, Share.Diggers, 0);
        world.Recruitment.SetShare(0, Share.Carriers, 1.0);
        Assert.Equal(1.0, world.Recruitment.ShareOf(0, Share.Carriers));
    }

    [Fact]
    public void Diggers_can_take_the_whole_when_the_others_stand_at_zero()
    {
        world.Recruitment.SetShare(0, Share.Carriers, 0);
        world.Recruitment.SetShare(0, Share.Builders, 0);
        world.Recruitment.SetShare(0, Share.Diggers, 1.0);
        Assert.Equal(1.0, world.Recruitment.ShareOf(0, Share.Diggers));
    }

    [Fact]
    public void Builders_can_take_the_whole_when_the_others_stand_at_zero()
    {
        world.Recruitment.SetShare(0, Share.Carriers, 0);
        world.Recruitment.SetShare(0, Share.Diggers, 0);
        world.Recruitment.SetShare(0, Share.Builders, 1.0);
        Assert.Equal(1.0, world.Recruitment.ShareOf(0, Share.Builders));
    }

    [Fact]
    public void A_full_budget_clamps_a_raise_until_another_share_is_lowered()
    {
        world.Recruitment.SetShare(0, Share.Diggers, 0.20);
        world.Recruitment.SetShare(0, Share.Builders, 0.40);
        world.Recruitment.SetShare(0, Share.Carriers, 0.40);
        Assert.Equal(0.40, world.Recruitment.ShareOf(0, Share.Carriers));
        Assert.Equal(0.40, world.Recruitment.ShareOf(0, Share.Builders));
        Assert.Equal(0.20, world.Recruitment.ShareOf(0, Share.Diggers));
        world.Recruitment.SetShare(0, Share.Diggers, 0.50);
        Assert.Equal(0.20, world.Recruitment.ShareOf(0, Share.Diggers));
        world.Recruitment.SetShare(0, Share.Builders, 0.10);
        Assert.Equal(0.10, world.Recruitment.ShareOf(0, Share.Builders));
        world.Recruitment.SetShare(0, Share.Diggers, 0.50);
        Assert.Equal(0.50, world.Recruitment.ShareOf(0, Share.Diggers));
    }

    [Fact]
    public void Untouched_shares_stay_at_their_defaults()
    {
        world.Recruitment.SetShare(0, Share.Diggers, 0.25);
        Assert.Equal(0.30, world.Recruitment.ShareOf(0, Share.Carriers));
        Assert.Equal(0.25, world.Recruitment.ShareOf(0, Share.Builders));
        Assert.Equal(0.25, world.Recruitment.ShareOf(0, Share.Diggers));
    }
}
