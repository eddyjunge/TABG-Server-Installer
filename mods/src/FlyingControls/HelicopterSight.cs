using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    public sealed class HelicopterSight : MonoBehaviour
    {
        private static readonly FieldInfo AlivePlayers = AccessTools.Field(typeof(PhotonServerHandler), "m_AlivePlayers");
        private static HelicopterSight _instance;
        private readonly Dictionary<int, MissileVisual> _missiles = new Dictionary<int, MissileVisual>();
        private readonly HashSet<int> _removed = new HashSet<int>();
        private bool _open, _guided = true;
        private int _target = -1;
        private float _lock, _readyAt, _nextAim, _pendingUntil, _lastReply;
        private TABGPlayerClient _local;
        private Vector3 _targetScreen;
        private ServerConnector _connector;
        private GUIStyle _label, _title;
        internal static bool Capturing => _instance && _instance._open && LocalHeli() && !Player.usingInterface && !TABGChat.inChat;
        private void Awake() { _instance = this; Debug.Log("[HeliMissiles] Sight ready: B, left fire, right mode; 40 damage / 4s per occupant"); }
        private static Car LocalHeli()
        {
            var p = Player.localPlayer;
            var seat = p?.m_sitting?.currentSeat;
            var car = seat ? seat.GetComponentInParent<Car>() : null;
            var local = PhotonServerHandler.instance?.LocalPlayer;
            return local != null && !local.IsDead && !local.IsDowned && car && MissileRules.IsHeli(car.name) ? car : null;
        }
        private void Send(byte kind, Action<BinaryWriter> write)
        {
            if (!_connector) _connector = UnityEngine.Object.FindObjectOfType<ServerConnector>();
            _connector?.SendMessageToServer((EventCode)VehicleProtocol.Event, VehicleProtocol.Message(kind, write), true);
        }
        private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 Read(BinaryReader r) => new Vector3(VehicleProtocol.ReadFinite(r), VehicleProtocol.ReadFinite(r), VehicleProtocol.ReadFinite(r));
        private void Update()
        {
            var handler = PhotonServerHandler.instance;
            if (_local != handler?.LocalPlayer)
            {
                foreach (var visual in _missiles.Values) if (visual) Destroy(visual.gameObject);
                _missiles.Clear(); _removed.Clear(); _open = false; _target = -1; _lock = 0; _readyAt = 0;
                _local = handler?.LocalPlayer;
            }
            var heli = LocalHeli();
            if (!heli || Player.usingInterface || TABGChat.inChat) { _open = false; _target = -1; _lock = 0; return; }
            if (Input.GetKeyDown(KeyCode.B)) { _open = !_open; _target = -1; _lock = 0; _nextAim = 0; }
            if (Input.GetKeyDown(KeyCode.Escape)) _open = false;
            if (!_open) return;
            var camera = Camera.main;
            if (!camera) return;
            if (Input.GetMouseButtonDown(1)) { _guided = !_guided; _target = -1; _lock = 0; }
            int target = _guided ? FindTarget(handler, camera, heli) : -1;
            if (target != _target) { _target = target; _lock = 0; if(MissileRules.IsVehicleTarget(target))Debug.Log($"[HeliMissiles] Vehicle sight candidate #{MissileRules.CarIndex(target)}"); }
            float now = Time.unscaledTime;
            if (now - _lastReply > .5f) _lock = 0;
            if (now >= _nextAim)
            {
                _nextAim = now + .1f;
                Send(VehicleProtocol.MissileAim, w => { w.Write(_target); Write(w, camera.transform.forward); });
            }
            if (Input.GetMouseButtonDown(0) && now >= _readyAt && now >= _pendingUntil && (!_guided || _lock >= 1))
            {
                _pendingUntil = now + .75f;
                Send(VehicleProtocol.MissileFire, w => { w.Write(_target); Write(w, camera.transform.forward); w.Write(_guided); });
            }
        }
        private int FindTarget(PhotonServerHandler handler, Camera camera, Car heli)
        {
            var players = AlivePlayers.GetValue(handler) as List<TABGPlayerClient>;

            int target = -1; float best = .975f;
            if(players!=null)foreach (var p in players)
            {
                if (p == _local || p.IsDead || (p.GroupIndex != 255 && p.GroupIndex == _local.GroupIndex) || (p.CurrentCar != null && p.CurrentCar == _local.CurrentCar)) continue;
                Vector3 point = p.PlayerPosition;
                if (p.PlayerObject)
                {
                    var hip = p.PlayerObject.GetComponentInChildren<Hip>();
                    if (hip) point = hip.transform.position;
                }
                Vector3 delta = point - camera.transform.position;
                float dot = Vector3.Dot(delta.normalized, camera.transform.forward);
                if (dot < best || delta.magnitude > MissileRules.Range) continue;
                bool blocked = false;
                foreach (var hit in Physics.RaycastAll(camera.transform.position, delta.normalized, delta.magnitude, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.GetComponentInParent<MissileVisual>() || hit.collider.transform.IsChildOf(heli.transform) ||
                        (Player.localPlayer && hit.collider.transform.IsChildOf(Player.localPlayer.transform)) ||
                        (p.PlayerObject && hit.collider.transform.IsChildOf(p.PlayerObject.transform))) continue;
                    blocked = true; break;
                }
                if (blocked) continue;
                best = dot; target = p.PlayerIndex; _targetScreen = camera.WorldToScreenPoint(point);
            }
            var cars=AircraftClient.Cars;
            if(cars!=null)foreach(var car in cars)
            {
                if(car==_local.CurrentCar || !car.CarReference || car.CarReference==heli)continue;
                var carTransform=car.CarReference.transform;
                var rig=car.CarReference.mainRig;
                if(!rig)continue;
                foreach(Vector3 point in VehicleGeometry.Get(car.CarIdentifier).AimPoints(camera.transform.position,camera.transform.forward,rig.position,rig.rotation))
                {
                Vector3 delta=point-camera.transform.position;
                float dot=Vector3.Dot(delta.normalized,camera.transform.forward);
                if(dot<best || delta.magnitude>MissileRules.Range)continue;
                bool blocked=false;
                foreach(var hit in Physics.RaycastAll(camera.transform.position,delta.normalized,delta.magnitude,~0,QueryTriggerInteraction.Ignore))
                {
                    if(hit.collider.GetComponentInParent<MissileVisual>() || hit.collider.transform.IsChildOf(carTransform) || hit.collider.transform.IsChildOf(heli.transform) ||
                        hit.collider.GetComponentInParent<Player>() || hit.collider.GetComponentInParent<NetworkPlayer>())continue;
                    blocked=true;break;
                }
                if(blocked)continue;
                best=dot;target=MissileRules.VehicleTarget(car.CarIndex);_targetScreen=camera.WorldToScreenPoint(point);
                }
            }
            return target;
        }
        internal static void Receive(byte kind, BinaryReader reader)
        {
            var self = _instance;
            if (!self) return;
            if (kind == VehicleProtocol.MissileReply)
            {
                float cooldown = VehicleProtocol.ReadFinite(reader), progress = VehicleProtocol.ReadFinite(reader);
                int target = reader.ReadInt32(); bool fired = reader.ReadBoolean();
                self._readyAt = Time.unscaledTime + Mathf.Clamp(cooldown, 0, MissileRules.Cooldown);
                float previousLock=self._lock;
                self._lock = target == self._target ? Mathf.Clamp01(progress) : 0;
                if(previousLock<1 && self._lock>=1 && self._open && self._guided)CombatAudio.Locked();
                if(previousLock<1 && self._lock>=1 && MissileRules.IsVehicleTarget(target))Debug.Log($"[HeliMissiles] Vehicle lock confirmed #{MissileRules.CarIndex(target)}");
                self._lastReply = Time.unscaledTime;
                if (fired) self._pendingUntil = 0;
                return;
            }
            int id = reader.ReadInt32(); Vector3 pos = Read(reader);
            if (kind == VehicleProtocol.MissileRemove)
            {
                if(self._removed.Contains(id))return;
                byte reason = reader.ReadByte();
                byte attacker=reader.BaseStream.Position<reader.BaseStream.Length?reader.ReadByte():(byte)255;
                if (self._missiles.TryGetValue(id, out var old) && old) Destroy(old.gameObject);
                self._missiles.Remove(id); self._removed.Add(id);
                if (self._removed.Count > 4096) self._removed.Clear();
                if(reason==2 && attacker!=255)FpvExplosion.Explode(pos,attacker,true,id);
                else if(reason!=0)MissileVisual.Burst(pos,reason==1);
                return;
            }
            Vector3 dir = Read(reader); bool guided = reader.ReadBoolean(); byte owner = reader.ReadByte();
            int tracking = reader.ReadInt32(); float age = VehicleProtocol.ReadFinite(reader);
            if (self._removed.Contains(id) || age >= MissileRules.FlightLifetime(guided)) return;
            if (!self._missiles.TryGetValue(id, out var visual) || !visual)
            {
                visual = MissileVisual.Create(id, pos, dir, guided);
                self._missiles[id] = visual;
            }
            visual.Sync(pos, dir, guided, tracking == self._local?.PlayerIndex || (self._local?.CurrentCar!=null && tracking==MissileRules.VehicleTarget(self._local.CurrentCar.CarIndex)));
        }
        internal static void ReportHit(int id, int bullet, Vector3 point)
            => _instance?.Send(VehicleProtocol.MissileHit, w => { w.Write(id); w.Write(bullet); Write(w, point); });
        private void OnGUI()
        {
            var heli = LocalHeli();
            if (!heli || Player.usingInterface || TABGChat.inChat) return;
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
                _label.normal.textColor = Color.white;
                _title = new GUIStyle(_label) { fontSize = 22, fontStyle = FontStyle.Bold };
            }
            float width = Mathf.Min(640, Screen.width - 32), x = (Screen.width - width) / 2;
            if (!_open) { GUI.Label(new Rect(x, Screen.height - 90, width, 30), "B  ·  Raketenvisier öffnen", _label); return; }
            float now = Time.unscaledTime, remaining = Mathf.Max(0, _readyAt - now);
            Color color = remaining > 0 ? new Color(1, .7f, .2f) : (!_guided || _lock >= 1 ? Color.green : Color.yellow);
            Box(new Rect(Screen.width / 2f - 12, Screen.height / 2f - 12, 24, 24), color);
            if (_guided && _target >= 0)
            {
                Box(new Rect(_targetScreen.x - 36, Screen.height - _targetScreen.y - 44, 72, 88), color);
                GUI.Label(new Rect(_targetScreen.x - 130, Screen.height - _targetScreen.y + 48, 260, 30),
                    _lock >= 1 ? (MissileRules.IsVehicleTarget(_target)?"FAHRZEUG ERFASST":"ZIEL ERFASST") : "Erfassen " + Mathf.RoundToInt(_lock * 100) + "%", _label);
            }
            GUI.Box(new Rect(x, Screen.height - 161, width, 143), GUIContent.none);
            GUI.Label(new Rect(x, Screen.height - 155, width, 32), _guided ? "ZIELSUCHE" : "GERADEAUS · SCHNELLER", _title);
            string status = remaining > 0 ? "Nachladen · " + remaining.ToString("0.0") + " s" :
                (now < _pendingUntil ? "Schuss wird bestätigt …" : (_guided && _lock < 1 ? "Spieler oder Fahrzeug anvisieren und kurz halten" : "BEREIT · Linksklick zum Abfeuern"));
            GUI.Label(new Rect(x, Screen.height - 120, width, 32), status, _label);
            var previous = GUI.color; GUI.color = color;
            GUI.DrawTexture(new Rect(x + 25, Screen.height - 80, (width - 50) * (1 - remaining / MissileRules.Cooldown), 5), Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(x, Screen.height - 68, width, 38), "Rechtsklick: Modus wechseln    ·    B: Schließen    ·    Munition: ∞", _label);
        }
        internal static void Box(Rect r, Color color)
        {
            var old = GUI.color; GUI.color = color;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - 2, r.width, 2), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, 2, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - 2, r.y, 2, r.height), Texture2D.whiteTexture);
            GUI.color = old;
        }
        [HarmonyPatch]
        internal static class WeaponPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(WeaponHandler), "PressAttack");
                yield return AccessTools.Method(typeof(WeaponHandler), "AltFireClick");
            }
            static bool Prefix(WeaponHandler __instance) => !Capturing || __instance.GetComponentInParent<Player>() != Player.localPlayer;
        }
        [HarmonyPatch(typeof(InputHandler), "get_IsADS")]
        internal static class AimPatch
        {
            static bool Prefix(InputHandler __instance, ref bool __result)
            {
                if (!Capturing || __instance.GetComponentInParent<Player>() != Player.localPlayer) return true;
                __result = false; return false;
            }
        }
        [HarmonyPatch(typeof(RaycastTrail), "LateUpdate")]
        internal static class BulletSweepPatch
        {
            private static readonly FieldInfo Last = AccessTools.Field(typeof(RaycastTrail), "lastPosition");
            private static readonly FieldInfo Projectile = AccessTools.Field(typeof(RaycastTrail), "projectileHit");
            private static readonly FieldInfo Mask = AccessTools.Field(typeof(RaycastTrail), "mask");
            private static readonly FieldInfo Radius = AccessTools.Field(typeof(RaycastTrail), "rayRadius");
            static bool Prefix(RaycastTrail __instance)
            {
                if ((!_instance || _instance._missiles.Count == 0) && !FpvClient.HasDrones) return true;
                var projectile = Projectile.GetValue(__instance) as ProjectileHit;
                if (!projectile) return true;
                Vector3 start = (Vector3)Last.GetValue(__instance), delta = __instance.transform.position - start;
                float distance = delta.magnitude;
                if (distance < .0001f) return true;
                float radius = Mathf.Max(.025f, (float)Radius.GetValue(__instance));
                RaycastHit nearest = default(RaycastHit); float first = float.PositiveInfinity;
                // Native bullets inherit global trigger settings. This explicit sweep makes rocket
                // interception independent of those settings without making rockets solid obstacles.
                foreach (var hit in Physics.SphereCastAll(start, radius, delta.normalized, distance, 1 << 2, QueryTriggerInteraction.Collide))
                    if ((hit.collider.GetComponentInParent<MissileVisual>() || hit.collider.GetComponentInParent<FpvBody>()) && hit.distance < first) { first = hit.distance; nearest = hit; }
                if (float.IsPositiveInfinity(first)) return true;
                int mask = (LayerMask)Mask.GetValue(__instance);
                foreach (var hit in Physics.SphereCastAll(start, radius, delta.normalized, first, mask, QueryTriggerInteraction.UseGlobal))
                {
                    if (hit.collider.GetComponentInParent<MissileVisual>() || hit.collider.GetComponentInParent<FpvBody>()) continue;
                    if (projectile.gunThatShotMe && hit.transform.root == projectile.gunThatShotMe) continue;
                    if (hit.distance < first - .001f) return true;
                }
                __instance.transform.position = nearest.point;
                projectile.Hit(nearest);
                Last.SetValue(__instance, nearest.point);
                return false;
            }
        }
        [HarmonyPatch(typeof(ProjectileHit), "Hit")]
        internal static class BulletPatch
        {
            private static readonly FieldInfo Control = AccessTools.Field(typeof(ProjectileHit), "m_HasControl");
            private static readonly FieldInfo Done = AccessTools.Field(typeof(ProjectileHit), "done");
            static bool Prefix(ProjectileHit __instance, RaycastHit hit)
            {
                var missile = hit.collider ? hit.collider.GetComponentInParent<MissileVisual>() : null;
                var drone = hit.collider ? hit.collider.GetComponentInParent<FpvBody>() : null;
                if (!missile && !drone) return true;
                if (!(bool)Done.GetValue(__instance))
                {
                    Done.SetValue(__instance, true);
                    if ((bool)Control.GetValue(__instance))
                    {
                        if(missile) ReportHit(missile.Id, __instance.GetInstanceID(), hit.point);
                        else FpvClient.Send(VehicleProtocol.DroneHit,w=>{w.Write(drone.Id);w.Write(__instance.GetInstanceID());Write(w,hit.point);});
                    }
                    MissileVisual.Burst(hit.point, true, .15f);
                    Destroy(__instance.gameObject);
                }
                return false;
            }
        }
    }

    public sealed class MissileVisual : MonoBehaviour
    {
        internal int Id;
        private Vector3 _position, _direction;
        private float _seen, _speed;
        private bool _incoming, _enlarged;
        private Transform _body;
        private SphereCollider _hitbox;
        private static Material _material;
        internal static Material Material
        {
            get
            {
                if (!_material) _material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Standard")) { color = new Color(1, .35f, .06f) };
                return _material;
            }
        }
        internal static MissileVisual Create(int id, Vector3 pos, Vector3 dir, bool guided)
        {
            var go = new GameObject("Heli missile " + id);
            go.transform.position = pos; go.transform.rotation = Quaternion.LookRotation(dir);
            var visual = go.AddComponent<MissileVisual>(); visual.Id = id;
            visual._body=RocketModel.Create(go.transform);
            var collider = go.AddComponent<SphereCollider>(); visual._hitbox = collider; collider.radius = MissileRules.HitRadius;
            // A dedicated bullet sweep handles this trigger; it never pushes a player or vehicle.
            go.layer = 2; collider.isTrigger = true;
            var trail = go.AddComponent<TrailRenderer>(); trail.sharedMaterial = Material;
            trail.time = .12f; trail.startWidth = .22f; trail.endWidth = .03f;
            trail.startColor = Color.yellow; trail.endColor = new Color(1, .2f, 0, 0);
            go.AddComponent<MissileSmoke>();
            CombatAudio.Spatial(go,"missile",.7f,5,160);
            visual.Sync(pos, dir, guided, false);
            return visual;
        }
        internal void Sync(Vector3 pos, Vector3 dir, bool guided, bool incoming)
        {
            _position = pos; _direction = dir; _seen = Time.unscaledTime;
            _speed = guided ? MissileRules.GuidedSpeed : MissileRules.StraightSpeed; _incoming = incoming;
            // Enlarge on the targeted player's client, including the actual bullet-hit surface.
            // Keep that size after tracking breaks so a defending player's target never shrinks mid-shot.
            if (guided && incoming && !_enlarged)
            {
                _enlarged = true;
                _body.localScale *= MissileRules.IncomingVisualScale;
                _hitbox.radius = MissileRules.IncomingHitRadius;
                GetComponent<TrailRenderer>().startWidth = .44f;
            }
        }
        private void Update()
        {
            float age = Time.unscaledTime - _seen;
            if (age > 1 || PhotonServerHandler.instance?.LocalPlayer == null) { Destroy(gameObject); return; }
            Vector3 expected = _position + _direction * _speed * Mathf.Min(age, .15f);
            transform.position = Vector3.Lerp(transform.position, expected, 1 - Mathf.Exp(-30 * Time.unscaledDeltaTime));
            transform.rotation = Quaternion.LookRotation(_direction);
            if(_incoming && Player.localPlayer && !Player.localPlayer.m_playerDeath.dead)CombatAudio.Incoming(Vector3.Distance(Player.localPlayer.transform.position,transform.position));
        }
        private void OnGUI()
        {
            if (!_incoming || PhotonServerHandler.instance?.LocalPlayer == null) return;
            var camera = Camera.main;
            if (!camera || Vector3.Distance(camera.transform.position, transform.position) > 130) return;
            var pos = camera.WorldToScreenPoint(transform.position);
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 17, fontStyle = FontStyle.Bold };
            style.normal.textColor = new Color(1, .4f, .1f);
            GUI.Label(new Rect(Screen.width / 2f - 190, 55, 380, 30), "RAKETE! Mit 3 Treffern abschießen", style);
            if (pos.z > 0) HelicopterSight.Box(new Rect(pos.x - 18, Screen.height - pos.y - 18, 36, 36), new Color(1, .3f, .1f));
        }
        internal static void Burst(Vector3 pos, bool intercepted, float size = 1)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.GetComponent<Collider>().enabled = false; Destroy(go.GetComponent<Collider>()); go.transform.position = pos;
            go.transform.localScale = Vector3.one * size;
            go.GetComponent<Renderer>().sharedMaterial = Material;
            Destroy(go, intercepted ? .1f : .18f);
        }
    }
}
