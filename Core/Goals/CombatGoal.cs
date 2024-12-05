using Core.GOAP;

using Microsoft.Extensions.Logging;
using SharpDX.WIC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using static System.MathF;

namespace Core.Goals;

public sealed class CombatGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 4f;
    public DateTime LastSafeLocationTime = new DateTime();
    LinkedList<Vector3> safeLocations = new LinkedList<Vector3>();

    private bool runningAway;
    private readonly ILogger<CombatGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly Navigation playerNavigation;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;
    private const float minAngleToTurn = PI / 35f;              // 5.14 degree
    private const float minAngleToStopBeforeTurn = PI / 2f;     // 90 degree
    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;

    public CombatGoal(ILogger<CombatGoal> logger, ConfigurableInput input,
        Wait wait, PlayerReader playerReader, StopMoving stopMoving, AddonBits bits,
        Navigation playerNavigation, ClassConfiguration classConfiguration, ClassConfiguration classConfig,
        CastingHandler castingHandler, CombatLog combatLog,
        IMountHandler mountHandler)
        : base(nameof(CombatGoal))
    {
        this.logger = logger;
        this.input = input;
        this.runningAway = false;

        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.combatLog = combatLog;
        this.playerNavigation = playerNavigation;
        playerNavigation.OnWayPointReached += Flee_SafePointReached;
        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.classConfig = classConfig;

        AddPrecondition(GoapKey.incombat, true);
        AddPrecondition(GoapKey.hastarget, true);
        //AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        //AddPrecondition(GoapKey.targettargetsus, true);
        AddPrecondition(GoapKey.incombatrange, true);

        AddEffect(GoapKey.producedcorpse, true);
        AddEffect(GoapKey.targetisalive, false);
        AddEffect(GoapKey.hastarget, false);

        Keys = classConfiguration.Combat.Sequence;
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent s && s.Key == GoapKey.producedcorpse)
        {
            // have to check range
            // ex. target died far away have to consider the range and approximate
            float distance = (lastMaxDistance + lastMinDistance) / 2f;
            SendGoapEvent(new CorpseEvent(GetCorpseLocation(distance), distance));
        }
    }

    private void ResetCooldowns()
    {
        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; i < span.Length; i++)
        {
            KeyAction keyAction = span[i];
            if (keyAction.ResetOnNewTarget)
            {
                keyAction.ResetCooldown();
                keyAction.ResetCharges();
            }
        }
    }

    public override void OnEnter()
    {
        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        lastDirection = playerReader.Direction;
    }

    public override void OnExit()
    {
        if (combatLog.DamageTakenCount() > 0 && !bits.Target())
        {
            stopMoving.Stop();
        }
    }

    public override void Update()
    {
        wait.Update();
        if (runningAway)
        {
            if (!bits.Combat())
            {
                runningAway = false;
                Console.WriteLine("NO longer in combat. We are safe!");
                safeLocations.AddLast(playerReader.MapPos);
                playerNavigation.Stop();
            }
            else
            {
                Console.WriteLine("Still running!");
                input.PressClearTarget();
                playerNavigation.Update();
                return;
            }
        }
        if (combatLog.DamageTaken.Count > 1)
        {
            // multiple mobs hitting us
            // bail
            Console.WriteLine("Multiple mob hits!");
            Console.WriteLine(safeLocations.Count);
            CancellationToken token = new CancellationToken();
            if (safeLocations.Count >= 1)
            {
                runningAway = true;
                Console.WriteLine("Current Pos: " + playerReader.MapPos.ToString());
                Console.WriteLine("Last safe: " + safeLocations.Last().ToString());
                playerNavigation.SetWayPoints(stackalloc Vector3[] { (Vector3)(safeLocations.Last()) });
                safeLocations.RemoveLast();
                input.PressClearTarget();
                Console.WriteLine("Running away to the last safe point!");
            } else
            {
                Console.WriteLine("No safe points to run, just fight!");
            }
        }
        if (MathF.Abs(lastDirection - playerReader.Direction) > MathF.PI / 2)
        {
            logger.LogInformation("Turning too fast!");
            stopMoving.Stop();
        }

        lastDirection = playerReader.Direction;
        lastMinDistance = playerReader.MinRange();
        lastMaxDistance = playerReader.MaxRange();

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        if (bits.Target())
        {
            if (bits.Target_Dead())
            {
                input.PressClearTarget();
            }
            if (classConfig.AutoPetAttack &&
                bits.Pet() &&
                (!playerReader.PetTarget() || playerReader.PetTargetGuid != playerReader.TargetGuid) &&
                input.PetAttack.GetRemainingCooldown() == 0)
            {
                input.PressPetAttack();
            }

            ReadOnlySpan<KeyAction> span = Keys;
            for (int i = 0; bits.Target_Alive() && i < span.Length; i++)
            {
                KeyAction keyAction = span[i];

                if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
                {
                    continue;
                }

                if (castingHandler.CastIfReady(keyAction,
                    keyAction.Interrupts.Count > 0
                    ? keyAction.CanBeInterrupted
                    : bits.Target_Alive))
                {
                    break;
                }
            }
        }

        if (!bits.Target())
        {
            logger.LogInformation("Lost target!");

            if (combatLog.DamageTakenCount() > 0 && !input.KeyboardOnly)
            {
                stopMoving.Stop();
                FindNewTarget();
            } else
            {
                Console.WriteLine("Target Dead2 -- saving safe pos " + playerReader.MapPos.ToString());
                safeLocations.AddLast(playerReader.MapPos);
                logger.LogWarning("---- Target dead, clearing");
                input.PressClearTarget();
            }
        }
    }

    private void Flee_SafePointReached()
    {
        Console.WriteLine("Safepoint reached, checking if combat cleared?");
        if (!bits.Combat()) {
            Console.WriteLine("We are safe!");
            runningAway = false;
        } else
        {
            Console.WriteLine("Still not safe, run to next safepoint!");
            if (safeLocations.Count >= 1)
            {
                playerNavigation.SetWayPoints(stackalloc Vector3[] { (Vector3)(safeLocations.Last()) });
                input.PressClearTarget();
            } else
            {
                Console.WriteLine("No more safepoints, Fight back!");
                runningAway = false;
                playerNavigation.Stop();
            }
        }
    }
    private void FindNewTarget()
    {
        if (playerReader.PetTarget() && combatLog.DeadGuid.Value != playerReader.PetTargetGuid)
        {
            ResetCooldowns();

            input.PressTargetPet();
            input.PressTargetOfTarget();
            wait.Update();

            if (!bits.Target_Dead())
            {
                logger.LogWarning("---- New target from Pet target!");
                return;
            }

            input.PressClearTarget();
        }

        if (combatLog.DamageTakenCount() > 1)
        {
            logger.LogInformation("Checking target in front...");
            input.PressNearestTarget();
            wait.Update();

            if (bits.Target() && !bits.Target_Dead())
            {
                if (bits.Target_Combat() && bits.TargetTarget_PlayerOrPet())
                {
                    stopMoving.Stop();
                    ResetCooldowns();

                    logger.LogWarning("Found new target!");
                    return;
                }

                input.PressClearTarget();
                wait.Update();
            }
            else if (combatLog.DamageTakenCount() > 0)
            {
                logger.LogWarning($"---- Possible threats from behind {combatLog.DamageTakenCount()}. Waiting target by damage taken!");
                wait.Till(2500, bits.Target);
            }
        }
    }

    private Vector3 GetCorpseLocation(float distance)
    {
        return PointEstimator.GetPoint(playerReader.MapPos, playerReader.Direction, distance);
    }
}
