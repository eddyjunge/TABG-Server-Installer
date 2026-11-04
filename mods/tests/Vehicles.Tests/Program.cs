using TabgInstaller.Vehicles;
int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
foreach (float dt in new[] { 0.01f, 0.02f, 0.04f })
foreach (float initial in new[] { -30f, -8f, -0.02f, 0f, 0.02f, 8f, 30f })
{
    float speed = initial;
    for (int i = 0; i < 1000; i++)
    {
        float previous = speed;
        speed = FlightMotion.BrakeVertical(speed, 11f, dt);
        Check(Math.Abs(speed) <= Math.Abs(previous), "Releasing vertical input must reduce speed");
        Check(speed * initial >= 0f, "Braking must not reverse descent into ascent");
    }
    Check(speed == 0f, "Released flight must settle at zero vertical speed");
}
Check(FlightMotion.BrakeVertical(0, 100, .02f) == 0, "Neutral flight cannot create lift");
Check(FlightMotion.BrakeVertical(-2, 0, .02f) < 0, "Old zero hover setting must not reverse descent");
using (var r = VehicleProtocol.Open(VehicleProtocol.Message(VehicleProtocol.Ack, w => w.Write(123)), out byte kind))
    Check(r != null && kind == VehicleProtocol.Ack && r.ReadInt32() == 123, "Spawn acknowledgement roundtrip");
foreach (var invalid in new[] { Array.Empty<byte>(), new byte[5], new byte[4097], new byte[10] })
    Check(VehicleProtocol.Open(invalid, out _) == null, "Reject invalid protocol headers");
var wrongVersion = VehicleProtocol.Message(VehicleProtocol.Hello); wrongVersion[4] = 99;
Check(VehicleProtocol.Open(wrongVersion, out _) == null, "Reject incompatible protocol version");
foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
{
    using var reader = VehicleProtocol.Open(VehicleProtocol.Message(VehicleProtocol.Spawn, w => w.Write(invalid)), out _);
    bool rejected = false;
    try { VehicleProtocol.ReadFinite(reader); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Reject nonfinite vehicle coordinates");
}


float oldConfigDescent = -60f;
for (int i = 0; i < 60; i++) oldConfigDescent = FlightMotion.BrakeVertical(oldConfigDescent, 0f, .02f);
Check(oldConfigDescent == 0f, "Legacy zero-hover configuration must stop descent within 1.2 seconds");
Console.WriteLine($"PASS: {checks} checks (descent release, ascent release, neutral flight, protocol validation)");

// Coupled limits must be inverses and cannot be bypassed by invalid/legacy settings.
foreach(float speed in new[]{15f,20f,30f,40f,45f})
{
    float height=VehicleBalance.HeightForSpeed(speed);
    Check(Math.Abs(VehicleBalance.SpeedForHeight(height)-speed)<.001f,"Altitude and speed must roundtrip");
    Check(VehicleBalance.HeightForSpeed(speed+1)<=height,"More speed must never allow more altitude");
}
Check(VehicleBalance.HeightForSpeed(80)==25,"Legacy speed 80 must be bounded");
Check(VehicleBalance.AircraftHealth==30,"Aircraft have exactly 30 health");
foreach(float hp in new[]{.1f,1f,25f,50f,75f,100f,150f})
{
    float after=VehicleBalance.SurvivorHealth(hp);
    Check(after>0 && after<=hp,"Explosion cannot kill or heal occupants");
    Check(hp-after<=50,"Occupants lose at most 50 health");
}
var life=new AircraftLife(0);
life.Start(10);
Check(!life.Damage(100,11.999f)&&life.Health==30,"Two seconds of full start protection");
life.Start(11.9f);
Check(!life.Damage(20,12)&&life.Health==10,"Changing/reentering seats cannot renew protection");
Check(life.Damage(10,12.1f),"Thirty total damage detonates once");
Check(!life.Damage(100,13),"Duplicate damage cannot detonate again");
var graph=new RoadGraph();
int a=graph.Add(new RoadPosition(0,0,0)), b=graph.Add(new RoadPosition(0,0,100)), c=graph.Add(new RoadPosition(100,0,100)), d=graph.Add(new RoadPosition(100,0,0));
graph.Connect(a,b);graph.Connect(b,c);graph.Connect(c,d);
Check(graph.Route(a,d)!.SequenceEqual(new[]{a,b,c,d}),"Autopilot follows the U-shaped road, not a direct shortcut");
int isolated=graph.Add(new RoadPosition(1,10,0));
Check(graph.Route(a,isolated)==null,"A nearby disconnected overpass is not a valid route");
graph.Connect(a,d);
Check(graph.Route(a,d)!.SequenceEqual(new[]{a,d}),"Route chooses the shortest connected road");
Check(graph.Route(-1,d)==null,"Missing road data returns no route");
Console.WriteLine("PASS: coupled limits, 30 HP, occupant survival, start protection, one detonation, street routing");

var boost=new HoverBoost();
Check(!boost.TryStart(0,true,false),"Stopped, passenger, or menu input cannot boost");
Check(boost.TryStart(1,true,true),"Moving driver can activate a ready boost");
Check(boost.Active(2.999f)&&!boost.Active(3),"Boost lasts exactly two seconds");
Check(!boost.TryStart(3,true,true)&&!boost.TryStart(10.999f,true,true),"Cooldown lasts eight seconds after boost ends");
Check(!boost.TryStart(11,false,true),"Holding W cannot retrigger when cooldown ends");
Check(boost.TryStart(11,true,true),"A fresh W press after cooldown boosts again");
boost.Stop();
Check(!boost.Active(11.5f),"Leaving the seat cancels active boost");
Check(!boost.TryStart(12,true,true),"Seat/marker changes cannot reset cooldown");
Check(boost.TryStart(21,true,true),"Cooldown expires after cancelling too");
Check(boost.CooldownRemaining(100)==0,"Cooldown display cannot become negative");
Console.WriteLine("PASS: hover boost duration, cooldown, input eligibility, held-key and seat-switch protection");

var missed=new RoadFollower(new[]{new RoadPosition(0,0,0),new RoadPosition(10,0,0),new RoadPosition(20,0,0),new RoadPosition(100,0,0)});
var recoveringPosition=new RoadPosition(0,0,0);
var recoveringVelocity=new RoadPosition(15,0,0);
for(int i=0;i<40;i++) { recoveringVelocity=missed.Step(recoveringPosition,recoveringVelocity,20,12,.02f); recoveringPosition.X+=recoveringVelocity.X*.02f; }
recoveringPosition=new RoadPosition(12,0,4); // Overshot the 10 m point and drifted off the road.
for(int i=0;i<150;i++)
{
    float previousProgress=missed.Progress;
    recoveringVelocity=missed.Step(recoveringPosition,recoveringVelocity,40,45,.02f);
    Check(recoveringVelocity.X>0,"Missing a vertex must not reverse travel");
    Check(missed.Progress>=previousProgress,"Route progress must not move backwards");
    recoveringPosition.X+=recoveringVelocity.X*.02f;recoveringPosition.Z+=recoveringVelocity.Z*.02f;
}
Check(Math.Abs(recoveringPosition.Z)<1,"Off-road recovery smoothly rejoins the road");

var bend=new List<RoadPosition>();
for(int x=0;x<=80;x+=4)bend.Add(new RoadPosition(x,0,0));
for(int i=1;i<=16;i++){float curveA=i*(float)Math.PI/32; bend.Add(new RoadPosition(80+30*(float)Math.Sin(curveA),0,30-30*(float)Math.Cos(curveA)));}
for(int z=34;z<=130;z+=4)bend.Add(new RoadPosition(110,0,z));
var follower=new RoadFollower(bend);
var pos=new RoadPosition(0,0,0);var vel=new RoadPosition(20,0,0);
float maxRoadError=0,minCornerSpeed=100,maxHeadingStep=0;
for(int i=0;i<1500 && !follower.Arrived;i++)
{
    float now=i*.02f;
    var before=vel;
    vel=follower.Step(pos,vel,now>=2 && now<4?40:20,now>=2 && now<4?45:12,.02f);
    pos.X+=vel.X*.02f;pos.Z+=vel.Z*.02f;
    if(pos.X>75 && pos.Z<45)
    {
        minCornerSpeed=Math.Min(minCornerSpeed,(float)Math.Sqrt(vel.X*vel.X+vel.Z*vel.Z));
        float error=float.MaxValue;
        for(int j=0;j+1<bend.Count;j++)
        {
            var curveA=bend[j];var curveB=bend[j+1];float dx=curveB.X-curveA.X,dz=curveB.Z-curveA.Z;
            float t=Math.Clamp(((pos.X-curveA.X)*dx+(pos.Z-curveA.Z)*dz)/(dx*dx+dz*dz),0,1);
            error=Math.Min(error,RoadFollower.Distance(pos,new RoadPosition(curveA.X+t*dx,0,curveA.Z+t*dz)));
        }
        maxRoadError=Math.Max(maxRoadError,error);
        float turn=Math.Abs((float)(Math.Atan2(vel.Z,vel.X)-Math.Atan2(before.Z,before.X)));
        maxHeadingStep=Math.Max(maxHeadingStep,turn);
    }
}
Check(follower.Arrived,"Boosted curved route must reach its destination");
Check(minCornerSpeed>4,"Gentle boosted curve must not cause curveA stop/pivot");
Check(maxRoadError<4,"Boosted curve must stay inside curveA normal road width");
Check(maxHeadingStep<.05f,"Heading must change continuously through the curve");
Console.WriteLine($"PASS: smooth follower; curve min speed={minCornerSpeed:F1}m/s, max road error={maxRoadError:F2}m, max heading step={maxHeadingStep*180/Math.PI:F2}deg");

var launcherA = new MissileWeapon(); var launcherB = new MissileWeapon();
Check(!launcherA.TryFire(0, false, false, -1), "No rockets outside a live helicopter seat");
Check(!launcherA.TryFire(0, true, true, 2), "Guided fire needs a lock");
Check(launcherA.Aim(0,2,true)==0, "Acquiring target starts at zero");
launcherA.Aim(.2f,2,true); launcherA.Aim(.4f,2,true); launcherA.Aim(.6f,2,true);
Check(!launcherA.TryFire(.6f,true,true,2), "Cannot fire before lock completes");
Check(launcherA.Aim(.7f,2,true)==1, "Continuous aim completes lock");
Check(launcherA.TryFire(.7f,true,true,2), "Locked rocket fires");
Check(launcherB.TryFire(.7f,true,false,-1), "Another occupant can fire in same frame");
Check(!launcherA.TryFire(.8f,true,false,-1), "Switching to straight flight cannot bypass cooldown");
Check(!launcherA.TryFire(4.69f,true,false,-1), "Full four-second cooldown enforced");
Check(launcherA.TryFire(4.71f,true,false,-1), "Unguided fire ready after four seconds");
launcherB.Aim(10,2,true); launcherB.Aim(10.2f,2,true); launcherB.Aim(10.4f,2,true); launcherB.Aim(10.7f,2,true);
Check(launcherB.Locked(10.7f,2), "A second independent lock can complete");
Check(!launcherB.Locked(11.1f,2), "Missing aim heartbeats expires lock");
Check(launcherB.Aim(11.2f,2,true)==0, "Returning to an expired target restarts acquisition");
Check(launcherB.Aim(11.3f,3,true)==0, "Changing target resets lock");
launcherB.Aim(11.4f,3,false);
Check(!launcherB.Locked(11.5f,3), "Cover or invalid target clears lock");
var armor = new MissileArmor();
Check(armor.Hit(1,123) && !armor.Destroyed, "One bullet does not destroy rocket");
Check(!armor.Hit(1,123) && armor.Hits==1, "Duplicate network hit is ignored");
Check(armor.Hit(2,123) && !armor.Destroyed, "Same local bullet ID from different shooter is independent");
Check(armor.Hit(1,124) && armor.Destroyed, "Third distinct bullet destroys rocket");
Check(!armor.Hit(1,125), "Destroyed rockets cannot take further hits");
Check(MissileRules.StraightSpeed > MissileRules.GuidedSpeed && MissileRules.Damage == 40, "Requested speed difference and damage");
Check(MissileRules.IsHeli("Heli(Clone)") && !MissileRules.IsHeli("Ufo"), "Missiles are helicopter-only");
var rayStart=new MissilePoint(0,0,0);var rayEnd=new MissilePoint(0,0,20);
Check(MissilePoint.SegmentHit(rayStart,rayEnd,new MissilePoint(0,0,10),1,out float contact) && Math.Abs(contact-.45f)<.001f, "Fast segment hits first sphere surface without tunnelling");
Check(!MissilePoint.SegmentHit(rayStart,rayEnd,new MissilePoint(2,0,10),1,out _), "A near miss does not damage player");
Check(!MissilePoint.SegmentHit(rayStart,new MissilePoint(0,0,8),new MissilePoint(0,0,10),1,out _), "Wall before victim blocks damage");
Check(!MissilePoint.SegmentHit(rayStart,rayEnd,new MissilePoint(0,0,-10),1,out _), "Players behind missile cannot be hit");
Check(MissilePoint.SegmentHit(rayStart,rayEnd,rayStart,1,out float inside) && inside==0,"Starting inside a target produces immediate contact");
var heading=new MissilePoint(0,0,1); var desiredHeading=new MissilePoint(1,0,0);
var steered=MissilePoint.Turn(heading,desiredHeading,.036f);
Check(Math.Abs(steered.Length-1)<.00001f && Math.Abs(Math.Acos(MissilePoint.Dot(heading,steered))-.036)<.0001, "Homing turn rate bounded while preserving speed");
Check(MissilePoint.Turn(heading,new MissilePoint(0,0,-1),.036f).Length>.999f, "Opposite direction does not create NaN or stall");
var rocketPos=new MissilePoint(0,0,0); var rocketHeading=new MissilePoint(0,0,1); bool interceptedTarget=false;
for(int i=0;i<400;i++)
{
    var targetPos=new MissilePoint(12 + i*.02f*2,0,90);
    rocketHeading=MissilePoint.Turn(rocketHeading,targetPos-rocketPos,MissileRules.TurnRadians*.02f);
    var next=rocketPos+rocketHeading*(MissileRules.GuidedSpeed*.02f);
    if(MissilePoint.SegmentHit(rocketPos,next,targetPos,.85f,out _)){interceptedTarget=true;break;}
    rocketPos=next;
}
Check(interceptedTarget,"Homing trajectory can intercept a moving target");
Console.WriteLine("PASS: helicopter missiles; independent cooldowns, lock/cover/target changes, three-hit interception, swept collision, limited homing, moving target");

// Reproduce repeated obstacle detections while altitude increases: the ceiling must not ratchet.
float hoverHeight=133;
for(int i=0;i<1000;i++)hoverHeight=HoverClearance.Target(133,hoverHeight+3);
Check(hoverHeight==193,"Repeated hover detections cannot climb above fixed surface-relative ceiling");
Check(HoverClearance.Target(133,130)==133,"Cleared obstacle returns to operating height");
Check(HoverClearance.HullClearance(133)==4,"Empty or displaced bounds cannot create a 133m hover clearance");
Check(HoverClearance.HullClearance(1.8f)==1.8f,"Valid hull clearance is preserved");
Check(HoverClearance.HullClearance(float.NaN)==1.5f,"Invalid bounds use finite hover clearance");
Console.WriteLine("PASS: bounded hover clearance and repeated obstacle detection without runaway ascent");

Check(DroneRules.Remaining(10,69.99f)>0 && DroneRules.Remaining(10,70)==0,"Drone battery expires after sixty seconds");
Check(DroneRules.Remaining(10,100)==0,"An expired battery stays empty");
Check(DroneRules.Signal(0)==1 && DroneRules.Signal(350)==1,"Drone has a clear signal nearby");
float lastSignal=1;
for(int distance=350;distance<=1000;distance++)
{
    float signal=DroneRules.Signal(distance);
    Check(signal<=lastSignal && signal>=0,"Signal degrades monotonically with distance");lastSignal=signal;
}
Check(DroneRules.Signal(1000)==0 && DroneRules.Signal(2000)==0,"Link ends at one kilometre");
float droneX=600,pilotX=250;
Check(DroneRules.Signal(Math.Abs(droneX-pilotX))==DroneRules.Signal(Math.Abs(droneX+500-(pilotX+500))),"A pilot following in a helicopter keeps the same signal");
Check(DroneRules.CanControl(true,false,false,false),"A living passenger can fly a drone");
Check(!DroneRules.CanControl(true,false,true,false),"The vehicle driver cannot control both at once");
Check(!DroneRules.CanControl(true,true,false,false) && !DroneRules.CanControl(false,false,false,false),"Downed and dead players cannot launch");
Check(!DroneRules.CanControl(true,false,false,true),"A second launch cannot replace an active drone");
var droneSequence=new DroneSequence();
Check(droneSequence.Accept(1) && !droneSequence.Accept(1) && !droneSequence.Accept(0),"Drone packets cannot replay or rewind motion");
Check(droneSequence.Finish() && !droneSequence.Finish() && !droneSequence.Accept(2),"Destroyed drones cannot finish twice or resume");
var droneArmor=new MissileArmor();
Check(droneArmor.Hit(1,10) && !droneArmor.Hit(1,10) && !droneArmor.Destroyed,"Duplicated bullet report counts only once");
Check(droneArmor.Hit(2,10) && !droneArmor.Destroyed && droneArmor.Hit(2,11) && droneArmor.Destroyed,"Third distinct bullet destroys drone, including different shooters");
foreach(float tilt in new[]{0f,20f,40f,60f})
{
    float upCosine=(float)Math.Cos(tilt*Math.PI/180),vertical=-4;
    for(int i=0;i<250;i++)vertical+=(DroneRules.LiftAcceleration(vertical,0,upCosine)*upCosine-9.81f)*.02f;
    Check(Math.Abs(vertical)<.001f,"Stabilized rotor thrust holds altitude while banked");
}
Check(DroneRules.LiftAcceleration(0,-7,1)<9.81f && DroneRules.LiftAcceleration(0,7,1)>9.81f,"Descent and lift commands change physical thrust");
foreach(int carId in new[]{0,1,255,1000,10000})
    Check(MissileRules.IsVehicleTarget(MissileRules.VehicleTarget(carId)) && MissileRules.CarIndex(MissileRules.VehicleTarget(carId))==carId,"Vehicle lock IDs roundtrip without overlapping players");
Check(!MissileRules.IsVehicleTarget(255) && !MissileRules.IsVehicleTarget(-1),"Player IDs and missing targets remain distinct from vehicles");
Console.WriteLine("PASS: FPV battery, moving pilot signal, passenger eligibility, stable physical lift, packet lifecycle, three bullet hits, vehicle lock IDs");

// Swept vehicle hull regression: high speed, grazing, start overlap and empty space.
var lo=new MissilePoint(-1,-1,-1);var hi=new MissilePoint(1,1,1);
Check(MissilePoint.SegmentBox(new MissilePoint(-20,0,0),new MissilePoint(20,0,0),lo,hi,out float boxT) && Math.Abs(boxT-.475f)<.00001f,"Fast missile cannot tunnel through hull");
Check(MissilePoint.SegmentBox(new MissilePoint(0,0,0),new MissilePoint(20,0,0),lo,hi,out boxT) && boxT==0,"Missile starting inside hull hits immediately");
Check(!MissilePoint.SegmentBox(new MissilePoint(-20,2,0),new MissilePoint(20,2,0),lo,hi,out _),"Parallel missile outside hull must miss");
Check(MissilePoint.SegmentBox(new MissilePoint(-20,1,0),new MissilePoint(-1,1,0),lo,hi,out boxT) && boxT==1,"Grazing endpoint hits hull");
Check(!MissilePoint.SegmentBox(new MissilePoint(2,0,0),new MissilePoint(20,0,0),lo,hi,out _),"Receding missile must not hit hull behind it");
Check(MissilePoint.SegmentBox(new MissilePoint(20,0,0),new MissilePoint(-20,0,0),lo,hi,out boxT) && Math.Abs(boxT-.475f)<.00001f,"Reverse direction hull intersection");
Console.WriteLine($"PASS: {checks} checks including swept vehicle collision regressions");

// FPV contact packets may have no movement: a hull overlap must still detonate.
float dronePadding=DroneRules.Radius+.4f;
var paddedMin=lo-new MissilePoint(dronePadding,dronePadding,dronePadding);
var paddedMax=hi+new MissilePoint(dronePadding,dronePadding,dronePadding);
var nearHull=new MissilePoint(1+DroneRules.Radius,0,0);
Check(MissilePoint.SegmentBox(nearHull,nearHull,paddedMin,paddedMax,out float droneT) && droneT==0,"Stopped drone at vehicle surface must validate its impact packet");
var awayHull=new MissilePoint(2,0,0);
Check(!MissilePoint.SegmentBox(awayHull,awayHull,paddedMin,paddedMax,out _),"Stopped drone in empty air must not fake a vehicle impact");
Check(!MissilePoint.SegmentBox(new MissilePoint(-3,0,0),new MissilePoint(-2,0,0),paddedMin,paddedMax,out _),"Drone segment ending before hull must not explode prematurely");
Console.WriteLine($"PASS: {checks} checks including FPV vehicle contact and stationary overlap");

foreach(float step in new[]{.01f,.02f,.04f})
foreach(float initial in new[]{-45f,-15f,0f,15f,45f})
{
    float v=initial;
    for(int i=0;i<(int)(3/step);i++)
    {
        float old=v;float braking=DroneRules.FlightAcceleration(v,0,step);braking=Math.Clamp(braking,-35,35);v+=braking*step;
        Check(Math.Abs(v)<=Math.Abs(old)+.00001f && v*initial>=0,"FPV release brakes without reversing or accelerating");
    }
    Check(Math.Abs(v)<.01f,"FPV release reaches stable hover even from maximum speed");
}
Check(DroneRules.FlightAcceleration(0,0,.02f)==0,"Looking around while stationary must not create motion");
Check(DroneRules.FlightAcceleration(10,25,.02f)>0 && DroneRules.FlightAcceleration(10,-25,.02f)<0,"FPV forward and reverse commands respond in correct direction");
Console.WriteLine($"PASS: {checks} including assisted FPV braking and neutral hover");

float previousLanding=0;
foreach(float clearance in new[]{0f,.1f,.2f,.5f,1f,3f,10f,150f})
{
    float descent=LandingRules.Descent(clearance);
    Check(descent>=previousLanding && descent<=LandingRules.MaxDescentSpeed && descent<VehicleBalance.CrashSpeed,"Automatic descent slows toward ground and remains below crash threshold");previousLanding=descent;
}
Check(LandingRules.Descent(.1f)==0 && LandingRules.Descent(.2f)<1,"Touchdown is soft and stops at ground clearance");
Check(LandingRules.Approach(100)==10 && LandingRules.Approach(0)==0,"Landing approach is bounded and stops at destination");
Console.WriteLine($"PASS: {checks} including landing approach and touchdown safety");

// Informal solo alliances: transitive, capped even when merging existing pairs.
var pacts=new BotAllianceRules();
Check(!pacts.Join(1,1),"Cannot ally with self");
Check(pacts.Join(1,2) && pacts.Join(2,3),"Pairs can invite a third bot");
Check(pacts.Friends(3,1) && pacts.Size(2)==3,"All three share symmetric friendship");
Check(!pacts.Join(3,4) && !pacts.Friends(1,4),"Fourth member must be refused atomically");
pacts.Clear();pacts.Join(1,2);pacts.Join(3,4);
Check(!pacts.Join(2,3) && pacts.Size(1)==2 && pacts.Size(3)==2,"Cannot merge two pairs into four");
pacts.Remove(2);Check(!pacts.Friends(1,2) && pacts.Size(1)==1,"Death removes stale friendship");
Check(pacts.Join(1,3) && pacts.Friends(1,4),"Survivor may join a different pair");
pacts.Clear();Check(!pacts.Friends(1,3),"Finale or round reset restores hostility");
Check(BotBlastRules.Damage(100,16,0,false,false,false,false)==100,"Direct FPV hit has dynamite base damage");
Check(BotBlastRules.Damage(100,16,8,false,false,false,false)==50,"Native linear blast falloff");
Check(BotBlastRules.Damage(100,16,17,true,true,false,false)==0,"Flat damage still respects radius");
Check(BotBlastRules.Damage(100,16,1,false,false,false,true)==0,"Walls block ordinary blast damage");
Check(BotBlastRules.Damage(40,10,5,false,true,false,true)==20,"Ignore-walls profile still uses damage falloff");
Check(BotBlastRules.Damage(100,16,1,true,true,true,false)==0,"Need-walls profile requires cover");
Check(BotBlastRules.Damage(100,16,1,true,false,true,true)==100,"Need-walls profile works behind cover");
foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1f})
    Check(BotBlastRules.Damage(100,16,invalid,false,false,false,false)==0,"Invalid distance cannot create damage");
Console.WriteLine($"PASS: {checks} total checks including bot alliances and authoritative blast rules");

var brave=new BotCombatMind(0);var careful=new BotCombatMind(2);
Check(brave.PushBias>careful.PushBias && careful.CoverBias>brave.CoverBias,"Personalities change tactics without changing skill");
for(int i=0;i<100;i++)brave.AddPressure(.16f);
Check(brave.Pressure==1 && brave.AimErrorMultiplier>1,"Pressure is capped and worsens precision");
Check(brave.PushBias<0 && brave.CoverBias>0,"Heavy fire discourages pushing and favors cover");
foreach(float dt in new[]{.01f,.033f,.1f})
{
 var mind=new BotCombatMind(1);mind.AddPressure(1);
 for(int i=0;i<(int)(7/dt);i++)mind.Tick(dt);
 Check(mind.Pressure==0,"Pressure expires consistently at different update rates");
}
foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1})
{
 float before=brave.Pressure;brave.Tick(invalid);brave.AddPressure(invalid);
 Check(brave.Pressure==before,"Invalid pressure/timestep cannot poison bot state");
}
Check(BotCombatMind.IncomingFire(30,1,true)>0,"Visible fire directed near bot creates pressure");
Check(BotCombatMind.IncomingFire(30,1,false)==0,"Wall-separated gunfire cannot apply near-miss pressure");
Check(BotCombatMind.IncomingFire(30,-1,true)==0,"Shots pointing away cannot suppress bot");
Check(BotCombatMind.IncomingFire(90,1,true)==0,"Far gunfire remains sound information only");
Check(careful.Reaction(1,0)>=.18f && careful.Reaction(.5f,100)>careful.Reaction(.5f,10),"Sight reacquisition is finite and slower at range");
Check(BotCombatMind.CanFlank(true,true,true,80,0,30,0),"Fresh allied engagement permits flanking");
Check(!BotCombatMind.CanFlank(false,true,true,80,0,30,0),"No solo omniscient flanking role");
Check(!BotCombatMind.CanFlank(true,false,true,80,0,30,0),"Unarmed bots must not flank into combat");
Check(!BotCombatMind.CanFlank(true,true,false,80,0,30,0),"No flanking from stale invisible target position");
Check(!BotCombatMind.CanFlank(true,true,true,20,0,30,0),"Low health cancels flank");
Check(!BotCombatMind.CanFlank(true,true,true,80,.7f,30,0),"Suppression cancels flank");
Check(!BotCombatMind.CanFlank(true,true,true,80,0,30,.8f),"Ring escape takes priority over flank");
Console.WriteLine($"PASS: {checks} total checks including bot personalities, suppression and flank eligibility");

// Regression cases from the two-player session: inventory stacks, exact blast identity and damage confirmation.
foreach(int count in new[]{1,2,5,99,int.MaxValue})
{
    Check(DroneRules.ThrowQuantity(DroneRules.ItemId,count)==1,"A drone launch consumes one grenade even from a stack");
    Check(DroneRules.ConsumedOne(count,count-1),"Native successful single throw authorizes a launch");
    Check(!DroneRules.ConsumedOne(count,count),"No inventory consumption cannot authorize a launch");
    Check(DroneRules.ThrowQuantity(42,count)==count,"Other throwable stacks retain native behavior");
}
Check(DroneRules.ThrowQuantity(DroneRules.ItemId,0)==0 && !DroneRules.ConsumedOne(0,-1),"An empty inventory must not yield a drone");
Check(!DroneRules.ConsumedOne(5,3),"Unexpected multi-item consumption must not be accepted silently");
foreach(bool missile in new[]{false,true})foreach(int length in new[]{10,20,33})
{
    var native=Enumerable.Range(0,length).Select(i=>(byte)(i*7)).ToArray();
    var packet=BlastReceipt.Tag(native,missile,456);
    Check(BlastReceipt.Read(packet,out bool source,out int id,out var restored) && source==missile && id==456 && restored.SequenceEqual(native),"Damage tags must preserve every native payload byte and weapon identity");
    Check(!BlastReceipt.Read(native,out _,out _,out _),"Untagged bullet damage must not acquire an explosive hitmarker");
    packet[^5]=3;Check(!BlastReceipt.Read(packet,out _,out _,out _),"Invalid source tag rejected");
}
Check(BlastReceipt.HealthLost(100,70)==30 && BlastReceipt.HealthLost(30,0)==30,"Hit confirmation uses actual health loss including lethal damage");
Check(BlastReceipt.HealthLost(70,100)==0 && BlastReceipt.HealthLost(50,50)==0,"Healing, blocked damage and misses must not show damage");
Check(BlastReceipt.HealthLost(float.NaN,0)==0 && BlastReceipt.HealthLost(100,float.PositiveInfinity)==0,"Nonfinite damage cannot produce a hitmarker");
var confirmation=new BlastConfirmationWindow(7,10);
Check(!confirmation.Accept(8,false,9,40,11),"Only the recorded shooter gets confirmation");
Check(!confirmation.Accept(7,false,7,40,11),"Self damage is not an enemy hit");
Check(!confirmation.Accept(7,false,9,0,11),"Zero damage has no hitmarker");
Check(confirmation.Accept(7,false,9,40,11),"Authoritative damage is confirmed");
Check(!confirmation.Accept(7,false,9,40,11.1f),"Repeated reports of the same victim do not double-confirm");
Check(confirmation.Accept(7,true,9,30,11),"Player and vehicle identifiers occupy separate namespaces");
Check(!confirmation.Accept(7,false,10,40,15.1f),"Late damage cannot be attributed to an expired blast");
Check(!confirmation.Accept(7,false,11,float.PositiveInfinity,12),"Infinite receipt damage rejected");
var otherBlast=new BlastConfirmationWindow(7,12);
Check(otherBlast.Accept(7,false,9,10,13),"A subsequent projectile can confirm damage to the same victim");
for(int firstMind=0;firstMind<3;firstMind++)for(int secondMind=0;secondMind<3;secondMind++)
{
    float peaceful=BotCombatMind.AllianceChance(firstMind,secondMind,false,false),strategic=BotCombatMind.AllianceChance(firstMind,secondMind,true,false);
    Check(peaceful>0 && strategic>peaceful && strategic<1,"A common enemy encourages but never guarantees a pact");
    Check(peaceful==BotCombatMind.AllianceChance(secondMind,firstMind,false,false),"Encounter outcome probability is independent of bot index/order");
    Check(BotCombatMind.AllianceChance(firstMind,secondMind,true,true)==0,"Bots already shooting each other do not abruptly ally");
}
Check(BotCombatMind.AllianceChance(0,1,false,false)>BotCombatMind.AllianceChance(0,0,false,false),"Reckless/tactical personalities complement one another");
Console.WriteLine($"PASS: {checks} total assertions including session feedback regressions");

// 1.5.3: parse the actual bot packet encoder as the native client does, including both quaternion sizes.
foreach(byte prefix in new byte[]{0,1,2,3,4,5,6,7})
{
 var rotation=new byte[prefix<4?7:1];rotation[0]=prefix;
 using var bytes=new MemoryStream();using var writer=new BinaryWriter(bytes);
 BotMovementPacket.WriteDriving(writer,12.5f,9,10,20,30,rotation,new byte[]{11,22,33},40,50,3);
 bytes.Position=0;using var reader=new BinaryReader(bytes);
 Check(reader.ReadSingle()==12.5f && reader.ReadByte()==1 && reader.ReadByte()==9,"Driving update time and player header");
 Check(reader.ReadByte()==64 && reader.ReadByte()==2,"Driving update native flags");
 Check(reader.ReadSingle()==10 && reader.ReadSingle()==20 && reader.ReadSingle()==30,"Driving update vehicle position");
 byte q=reader.ReadByte();if(q<4)Check(reader.ReadBytes(6).Length==6,"Full compressed quaternion consumed");
 Check(reader.ReadBytes(3).SequenceEqual(new byte[]{11,22,33}),"Direction follows quaternion without shifting");
 Check(reader.ReadSingle()==40 && reader.ReadSingle()==50 && reader.ReadByte()==3,"Player aim and vehicle state stay aligned");
 Check(reader.ReadByte()==0 && bytes.Position==bytes.Length,"Native client can read trailing unoccupied car count without EndOfStream");
}
Check(!BotSeparation.SafeStep(0,1,0,1.5f),"Follower cannot walk into stationary player");
Check(!BotSeparation.SafeStep(0,10,0,5),"Large movement steps cannot tunnel through a player");
Check(BotSeparation.SafeStep(0,-.2f,0,.5f),"Overlapping bot can retreat");
Check(!BotSeparation.SafeStep(0,.2f,0,.5f),"Overlapping bot cannot move deeper into another player");
Check(BotSeparation.SafeStep(.2f,0,0,0),"Coincident bots can separate");
// Stationary front bot: the rear bot must pass it and continue toward its target.
foreach(float dt in new[]{.016f,.05f,.1f})
{
 float x=0,z=-5;
 for(int tick=0;tick<(int)(10/dt);tick++)
 {
  float dx=-x,dz=8-z,len=MathF.Sqrt(dx*dx+dz*dz);if(len<.3f)break;
  BotSeparation.Steer(dx/len,dz/len,-x,-z,out dx,out dz);
  float sx=dx*4*dt,sz=dz*4*dt;
  if(BotSeparation.SafeStep(sx,sz,-x,-z)){x+=sx;z+=sz;}
  Check(x*x+z*z>=BotSeparation.BodyDistance*BotSeparation.BodyDistance-.0001f,"Follower maintains body clearance while passing");
 }
 Check(z>6,"Follower gets around standing bot instead of remaining blocked");
 // Head-on approaches choose opposite physical sides and make progress.
 float ax=0,az=-5,bx=0,bz=5;
 for(int tick=0;tick<(int)(5/dt);tick++)
 {
  BotSeparation.Steer(0,1,bx-ax,bz-az,out float adx,out float adz);
  BotSeparation.Steer(0,-1,ax-bx,az-bz,out float bdx,out float bdz);
  if(BotSeparation.SafeStep(adx*3*dt,adz*3*dt,bx-ax,bz-az)){ax+=adx*3*dt;az+=adz*3*dt;}
  if(BotSeparation.SafeStep(bdx*3*dt,bdz*3*dt,ax-bx,az-bz)){bx+=bdx*3*dt;bz+=bdz*3*dt;}
  Check((ax-bx)*(ax-bx)+(az-bz)*(az-bz)>=BotSeparation.BodyDistance*BotSeparation.BodyDistance-.0001f,"Head-on bots maintain clearance");
 }
 Check(az>4 && bz< -4,"Head-on bots pass each other");
}
Console.WriteLine($"PASS: {checks} total assertions including 1.5.3 bot crowd and native movement packet regressions");

// 1.6.0: independent teams, stable colors, weighted skill and finite shield collision.
var colors=new BotAllianceRules();
Check(colors.Join(10,11) && colors.Join(10,12),"First test creates a three-bot pact");
byte firstColor=colors.TeamId(10);
Check(firstColor!=0 && firstColor==colors.TeamId(11) && firstColor==colors.TeamId(12),"Pact members share one color");
Check(colors.Join(20,21) && colors.Join(20,22),"Second test creates an independent pact");
Check(colors.TeamId(20)!=firstColor && !colors.Friends(10,20),"Repeated team tests remain enemies with distinct colors");
Check(!colors.Join(10,20) && !colors.Join(10,30),"Merging groups cannot exceed three bots");
colors.Remove(10);
Check(colors.TeamId(11)==firstColor && colors.TeamId(12)==firstColor,"Leader death preserves surviving team color");
colors.Remove(11);
Check(colors.TeamId(12)==0 && colors.TeamId(20)!=0,"Last survivor becomes solo without changing another group");
colors.Clear();Check(colors.TeamId(20)==0 && !colors.Friends(20,21),"Round reset clears pacts and colors");
var skillCounts=new int[6];
for(int i=0;i<10000;i++)skillCounts[BotSkillRules.Roll((i+.5f)/10000)]++;
Check(skillCounts[1]==1500 && skillCounts[2]==2500 && skillCounts[3]==3500 && skillCounts[4]==1800 && skillCounts[5]==700,"Skill weights remain 15/25/35/18/7 percent");
for(int level=2;level<=5;level++)Check(BotSkillRules.MistakeChance(level)<BotSkillRules.MistakeChance(level-1),"Higher skill reduces poor decisions");
Check(BotSkillRules.RescueRisk(5,10,false,100)==0,"Critical rescuer first preserves own life");
Check(BotSkillRules.RescueRisk(5,80,true,5)==0 && BotSkillRules.RescueRisk(1,80,true,5)>0,"Novice can attempt an exposed rescue a pro refuses");
Check(BotSkillRules.RescueRisk(5,80,false,5)==1,"Cover allows a safe pro rescue");
Check(BotSkillRules.Upgrade(0,1,true,false,0,10,20),"Unarmed bot accepts its first weapon");
Check(!BotSkillRules.Upgrade(80,60,false,false,30,30,0),"Loaded bot does not downgrade pointlessly");
Check(BotSkillRules.Upgrade(80,70,false,true,30,30,0),"Empty bot may use a slightly weaker usable weapon");
Check(BotSkillRules.Upgrade(80,80,false,false,55,10,8),"Close combat can justify switching from sniper to close-range weapon");
Check(BotShieldRules.FirstHit(-10,0,0,1,0,0,6,20)==4,"Bullet crossing entire shield hits near face");
Check(BotShieldRules.FirstHit(0,0,0,1,0,0,6,20)==6,"Bullet leaving shield hits inside face");
Check(float.IsPositiveInfinity(BotShieldRules.FirstHit(-10,0,0,1,0,0,6,3)),"Bullet cannot hit shield beyond this simulation step");
Check(float.IsPositiveInfinity(BotShieldRules.FirstHit(-10,7,0,1,0,0,6,20)),"Bullet beside shield remains unblocked");
Check(float.IsPositiveInfinity(BotShieldRules.FirstHit(10,0,0,1,0,0,6,20)),"Shield behind bullet does not absorb shot");
Console.WriteLine($"PASS: {checks} assertions including bot teams, skill decisions and shield collision");

// Ground armor must not repair accumulated aircraft damage on repeated takeoffs.
var groundedHeli=new AircraftLife(0);groundedHeli.SetGrounded(true);
Check(groundedHeli.Health==300 && groundedHeli.Maximum==300,"Unused grounded helicopter starts with 300 HP");
groundedHeli.Damage(60,3);groundedHeli.SetGrounded(false);
Check(groundedHeli.Health==24 && groundedHeli.Maximum==30,"Ground damage persists proportionally on takeoff");
groundedHeli.Damage(6,4);groundedHeli.SetGrounded(true);
Check(groundedHeli.Health==180,"Landing preserves flight damage, not a free repair");
for(int i=0;i<1000;i++){groundedHeli.SetGrounded(true);groundedHeli.SetGrounded(false);groundedHeli.SetGrounded(true);}
Check(Math.Abs(groundedHeli.Health-180)<.001f,"Repeated ground samples and takeoffs do not heal the aircraft");
Check(groundedHeli.Damage(300,5),"Hard impact still destroys an armored grounded heli");
groundedHeli.SetGrounded(false);groundedHeli.SetGrounded(true);
Check(groundedHeli.Destroyed && groundedHeli.Health==0,"Destroyed helicopter cannot revive through ground-state changes");
Check(new AircraftLife(0).Health==30,"UFO keeps its original 30 HP unless explicitly classified as grounded heli");
foreach(float step in new[]{.01f,.02f,.05f,.1f})
{
 float altitude=150,distance=80;
 for(int i=0;i<4000;i++)
 {
  float vertical=LandingRules.Descent(altitude),travel=LandingRules.Approach(distance);
  Check(Math.Sqrt(vertical*vertical+travel*travel)<VehicleBalance.CrashSpeed,"Even diagonal landing motion stays below hard-crash threshold");
  altitude=Math.Max(0,altitude-vertical*step);distance=Math.Max(0,distance-travel*step);
 }
 Check(altitude<.18f && distance<.4f,"Automatic approach reaches soft touchdown at multiple physics rates");
}
Check(!BotTravelRules.FitsSquad(1,3) && !BotTravelRules.FitsSquad(2,3),"Bike and two-seater cannot split a three-bot squad");
Check(BotTravelRules.FitsSquad(4,3) && BotTravelRules.FitsSquad(1,1),"Squads may share a heli; solos may ride a bike");
Check(!BotTravelRules.FitsSquad(0,0),"No valid transport for an empty/dead squad");
Check(BotTravelRules.LaunchCost(20,250,1)<270*.7f,"Nearby forward pad benefits a long rotation");
Check(BotTravelRules.LaunchCost(150,80,1)>80*.7f,"Walking away from a nearby destination is not useful");
Check(float.IsPositiveInfinity(BotTravelRules.LaunchCost(10,300,-1)),"Pad facing away from target is rejected");
Check(float.IsPositiveInfinity(BotTravelRules.LaunchCost(350,300,1)),"Very distant pad is not worth walking to");
Check(float.IsPositiveInfinity(BotTravelRules.LaunchCost(float.NaN,300,1)),"Invalid pad position is rejected");
Console.WriteLine($"PASS: {checks} total checks including ground armor, landing and squad travel regressions");

// Round strategy: enough equipment must lead to searching, but bad loot must not trap anyone.
Check(!BotRoundRules.LeaveLoot(true,10) && BotRoundRules.LeaveLoot(true,22),"Ready squad gets a short equip window, then actively searches");
Check(BotRoundRules.LeaveLoot(false,65),"Missing supplies cannot cause endless looting in one place");
Check(!BotRoundRules.Ready(false,30,30,100),"Unarmed bot does not claim to be combat ready");
Check(!BotRoundRules.Ready(true,0,30,100),"Empty weapons do not count as combat-ready equipment");
Check(BotRoundRules.Ready(true,2,1,22),"Shotgun readiness uses its magazine rather than rifle ammo counts");
Check(BotRoundRules.Ready(true,15,30,40),"A usable partial loadout can leave after a longer loot window");
Check(BotRoundRules.Sprint(30,false,false,true),"Long unopposed travel uses sprint");
Check(!BotRoundRules.Sprint(2,false,false,true) && !BotRoundRules.Sprint(30,true,false,true) && !BotRoundRules.Sprint(30,false,true,true),"No sprint during close pickup, aiming or healing");
Check(BotRoundRules.SprintFlag(1,true)==9 && BotRoundRules.SprintFlag(129,true)==137,"Sprint uses native bit 3 and preserves jump bit 7");
Check(BotRoundRules.SprintFlag(2,true)==2 && BotRoundRules.SprintFlag(9,false)==1,"No backwards sprint and sprint clears when disabled");
Check(BotRoundRules.Rotate(200,30,false),"Rotation begins before travel time plus margin exceeds the zone deadline");
Check(!BotRoundRules.Rotate(0,1,true),"Bot already well inside safe area does not flee needlessly");
Check(BotRoundRules.Rotate(10,100,true),"Moving wall makes an exposed bot rotate immediately");
Check(!BotRoundRules.TripFits(400,40) && BotRoundRules.TripFits(100,60),"Travel planner rejects journeys that consume the safe time budget");
float fresh=BotRoundRules.PoiScore(300,2,1000,.5f,0),searched=BotRoundRules.PoiScore(300,2,10,.5f,0);
Check(searched>fresh+200,"Recently empty hotspots get a substantial revisit penalty");
Check(BotRoundRules.PoiScore(100,1,1000,.5f,0)<BotRoundRules.PoiScore(800,2,1000,.5f,0),"Nearby smaller places can beat a distant popular hotspot");
foreach(int budget in new[]{1,3,8,32})
{
 // A wall blocks the direct approach but has a doorway at z=4.
 var route=new BotGridSearch(8,0,12,(a,b)=>b.X!=4 || b.Z==4);
 int iterations=0;
 while(!route.Done && iterations++<700){int before=route.Expanded;route.Step(budget);Check(route.Expanded-before<=budget,"Grid search respects each supplied frame budget");}
 Check(route.Reached && route.Path.Any(p=>p.X==4 && p.Z==4),"Bot detours through the actual doorway instead of stopping at the wall");
 Check(route.Path.All(p=>p.X!=4 || p.Z==4),"Detour never crosses the blocked wall");
 for(int i=1;i<route.Path.Count;i++)Check(Math.Abs(route.Path[i].X-route.Path[i-1].X)+Math.Abs(route.Path[i].Z-route.Path[i-1].Z)==1,"No route teleports or cuts corners");
}
// Long Area-style walls require first moving away from the destination.
foreach(int side in new[]{-1,1})
{
 var longWall=new BotGridSearch(0,8,48,(a,b)=> !(b.Z==3 && b.X*side<25),4096);
 int frames=0;
 while(!longWall.Done && frames++<5000){int before=longWall.Expanded;longWall.Step(3);Check(longWall.Expanded-before<=3,"Long wall respects incremental work budget");}
 Check(longWall.Reached && longWall.Path.Any(c=>c.X*side>=25),"Route goes around a 50 metre wall instead of pushing through it");
 for(int j=1;j<longWall.Path.Count;j++)Check(Math.Abs(longWall.Path[j].X-longWall.Path[j-1].X)+Math.Abs(longWall.Path[j].Z-longWall.Path[j-1].Z)==1,"Wall route has contiguous cardinal steps");
}
var partialRoute=new BotGridSearch(12,0,16,(a,b)=>b.X<8,4096);
while(!partialRoute.Done)partialRoute.Step(5);
Check(!partialRoute.Reached && partialRoute.Path.Count>4 && partialRoute.Path.Last().X==7,"Unreachable destination provides a reachable partial route without claiming arrival");
Check(!BotRoundRules.CanRevive(1) && !BotRoundRules.CanRevive(0) && BotRoundRules.CanRevive(2) && BotRoundRules.CanRevive(4),"Only real team modes allow down and revive");
Check(BotRoundRules.ReserveTarget(1)==12 && BotRoundRules.ReserveTarget(30)==90 && BotRoundRules.ReserveTarget(100)==120,"Supply target adapts to magazines and caps hoarding");
Check(!BotRoundRules.GrenadeOpportunity(4,true,3,true,0) && !BotRoundRules.GrenadeOpportunity(60,true,3,true,0),"Grenade tactics retain useful throw distance limits");
Check(BotRoundRules.GrenadeOpportunity(20,true,1,false,1) && BotRoundRules.GrenadeOpportunity(20,false,2,false,1) && BotRoundRules.GrenadeOpportunity(20,false,1,true,1),"Cover, grouped enemies and pushes create grenade opportunities");
Check(BotRoundRules.GrenadeOpportunity(20,false,1,false,.4f) && !BotRoundRules.GrenadeOpportunity(20,false,1,false,.9f),"Ordinary grenade opportunities remain varied");
var doorway=new BotGridSearch(16,0,24,(a,b)=>
 !(Math.Abs(b.X)==8 && Math.Abs(b.Z)<=8 && !(b.X==8 && b.Z==1)) &&
 !(Math.Abs(b.Z)==8 && Math.Abs(b.X)<=8),4096);
while(!doorway.Done)doorway.Step(2);
Check(doorway.Reached && doorway.Path.Any(c=>c.X==8 && c.Z==1),"Fine local search exits an enclosure through its narrow door");
var sealedRoom=new BotGridSearch(4,0,6,(a,b)=>b.X<2);
while(!sealedRoom.Done)sealedRoom.Step(8);
Check(!sealedRoom.Reached && sealedRoom.Expanded<=625,"Unreachable destination fails within bounded work");
var cliff=new BotGridSearch(5,0,6,(a,b)=>!(a.X==2 && b.X==3 || a.X==3 && b.X==2));
while(!cliff.Done)cliff.Step(8);
Check(!cliff.Reached,"Blocked terrain edge cannot be bypassed by a one-way route");
Console.WriteLine($"PASS: {checks} total checks including 1.7.0 sprint, round strategy, zone deadlines and bounded detours");

Check(!BotRoundRules.ImmediateDanger(false,false,true,4),"Hidden footsteps alone do not prevent an unarmed bot looting");
Check(!BotRoundRules.ImmediateDanger(false,true,false,4),"An unarmed enemy sighting does not cause permanent panic");
Check(BotRoundRules.ImmediateDanger(true,false,false,100),"Recent damage overrides kit collection even without visibility");
Check(BotRoundRules.ImmediateDanger(false,true,true,12),"Close visible armed enemy remains an immediate danger");
Check(!BotRoundRules.ImmediateDanger(false,true,true,60),"Distant sighting allows essential equipment collection");
Console.WriteLine($"PASS: {checks} total checks including 1.7.1 immediate-danger and loot regressions");

var recovery=new BotRecoveryState();
for(int i=0;i<14;i++)Check(recovery.Tick(.1f,true,false,false,false,false)==BotRecoveryEvent.None,"Short blockage does not trigger recovery early");
Check(recovery.Tick(.11f,true,false,false,false,false)==BotRecoveryEvent.Start && recovery.Active,"Stalled movement starts an independent recovery phase");
Check(recovery.Tick(.3f,false,false,false,false,false)==BotRecoveryEvent.None && recovery.Active,"Tactical intent changes cannot cancel active recovery");
Check(recovery.Tick(.1f,true,true,false,false,true)==BotRecoveryEvent.Finished && !recovery.Active,"Recovery ends after measured arrival");
for(int i=0;i<50;i++)Check(recovery.Tick(.1f,true,false,true,false,false)==BotRecoveryEvent.None,"Bounded pending planning grace avoids premature recovery");
for(int i=0;i<16;i++)recovery.Tick(.1f,true,false,false,false,false);
Check(recovery.Active,"Recovery also starts for nearby unreachable goals");
Check(recovery.Tick(3.1f,true,false,false,false,false)==BotRecoveryEvent.Failed && !recovery.Active,"Failed recovery has a finite deadline");
for(int i=0;i<50;i++)Check(recovery.Tick(.1f,true,true,false,false,false)==BotRecoveryEvent.None,"Real horizontal progress prevents false recovery");
Console.WriteLine($"PASS: {checks} total checks including movement recovery lifecycle");
