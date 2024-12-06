using Core.GOAP;

using Microsoft.Extensions.Logging;
using SharpDX.WIC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Drawing;
using static System.MathF;
using SixLabors.ImageSharp.Formats;

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

        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.combatLog = combatLog;
        this.playerNavigation = playerNavigation;
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
        playerNavigation.Update();

        lastDirection = playerReader.Direction;
        lastMinDistance = playerReader.MinRange();
        lastMaxDistance = playerReader.MaxRange();

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        if ((MathF.Abs(lastDirection - playerReader.Direction) > MathF.PI / 2))
        {
            logger.LogInformation("Turning too fast!");
            stopMoving.Stop();

        }
        if (bits.Target() && !bits.Target_Alive())
        {
            input.PressClearTarget();
            return;
        }

        if (combatLog.DamageTaken.Count > 1 || (playerReader.HealthPercent() < 50 && playerReader.ManaPercent() < 20) || (playerReader.HealthPercent() < 30))
        {
            // multiple mobs hitting us
            // bail
            Console.WriteLine("Multiple mob hits! OR HP and mana low");
            Console.WriteLine(safeLocations.Count);
            if (safeLocations.Count >= 1)
            {
                bool foundPoint = false;
                Console.WriteLine("Current Pos: " + playerReader.MapPos.ToString());
                Console.WriteLine("Safe Spots: " + safeLocations.Count);
                for (LinkedListNode<Vector3> point = safeLocations.Last; point != null; point = point.Previous)
                {
                    Vector2 p1 = new Vector2(point.Value.X, point.Value.Y);
                    Vector2 p2 = new Vector2(playerReader.MapPos.X, playerReader.MapPos.Y);
                    if (Vector2.Distance(p1, p2) >= 1.8)
                    {
                        // select the point far enough to lose the current mobs.
                        input.PressClearTarget();
                        playerNavigation.Stop();
                        playerNavigation.StuckResetTimeout = 500;
                        playerNavigation.ResetStuckParameters();
                        playerNavigation.SetWayPoints(stackalloc Vector3[] { (Vector3)(point.Value) });
                        playerNavigation.Update();
                        Console.WriteLine("Found point " + point.Value.ToString());
                        foundPoint = true;
                        break;
                    }
                }
                if (foundPoint)
                {
                    Console.WriteLine("Running away to the last safe point!");
                    return;
                }
            }
        }

        if (bits.Target())
        {
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
                if (LastSafeLocationTime == DateTime.MinValue)
                {
                    LastSafeLocationTime = DateTime.UtcNow;
                    safeLocations.AddLast(playerReader.MapPos);
                }
                else
                {
                    if ((DateTime.UtcNow - LastSafeLocationTime).TotalMilliseconds > 7_000 && !bits.Combat())
                    {
                        safeLocations.AddLast(playerReader.MapPos);
                        LastSafeLocationTime = DateTime.UtcNow;
                        if (safeLocations.Count > 100)
                        {
                            safeLocations.RemoveFirst();
                        }
                    }
                }
                Console.WriteLine("Target Dead2 -- saving safe pos " + playerReader.MapPos.ToString());
                safeLocations.AddLast(playerReader.MapPos);
                logger.LogWarning("---- Target dead, clearing");
                input.PressClearTarget();
            }
        }
    }

    private void FindNewTarget()
    {
        playerNavigation.Stop();
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
