using YodaStoriesNG.Engine.Data;

namespace YodaStoriesNG.Engine.Game;

public unsafe partial class GameEngine
{
    // Some native puzzles replace the hero with a scripted cursor/portrait while
    // accepting bump choices. Visibility alone is not an input lock in that mode.
    // Require the authored counter controller, variable input, and variable-driven
    // ShowHero exit together; ordinary hidden travel sequences remain locked.
    private bool HasAuthoredBumpStance(int x,int y) =>
        Math.Max(Math.Abs(x-_state.PlayerX),Math.Abs(y-_state.PlayerY))==1 &&
        _state.CurrentZone!.Actions.Where((a,i)=>!_state.DisabledActions.Contains((_state.CurrentZoneId,i))).Any(a=>
            a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=2 && c.Arguments[0]==x && c.Arguments[1]==y) &&
            a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump && c.Arguments.Count>=3 &&
                _state.CurrentZone.GetTile(x,y,1)==unchecked((ushort)c.Arguments[2])) &&
            a.Conditions.Where(c=>c.Opcode==ConditionOpcode.HeroIsAt).All(c=>c.Arguments.Count>=2 &&
                c.Arguments[0]==_state.PlayerX && c.Arguments[1]==_state.PlayerY));

    private bool HeroControlAvailable
    {
        get
        {
            if (_state.HeroVisible) return true;
            if (_actionExecutor?.IsBusy == true || _state.CurrentZone is not { } zone) return false;
            var active=zone.Actions.Where((a,i)=>!_state.DisabledActions.Contains((zone.Id,i))).ToList();
            return active.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.CounterIs && c.Arguments.Count>0 &&
                    c.Arguments[0]>0 && c.Arguments[0]==_state.GetCounter(zone.Id)) &&
                    a.Instructions.Any(i=>i.Opcode==InstructionOpcode.MoveHeroTo) &&
                    a.Conditions.Any(c=>c.Opcode==ConditionOpcode.IsVariable)) &&
                active.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.Bump) &&
                    a.Instructions.Any(i=>i.Opcode==InstructionOpcode.SetVariable)) &&
                active.Any(a=>a.Conditions.Any(c=>c.Opcode==ConditionOpcode.IsVariable) &&
                    a.Instructions.Any(i=>i.Opcode is InstructionOpcode.ShowHero or InstructionOpcode.ChangeZone));
        }
    }
}
