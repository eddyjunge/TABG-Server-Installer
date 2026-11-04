using System;
using System.IO;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    public sealed class BlastHitmarker : MonoBehaviour
    {
        private static bool _tagging,_missile;
        private static int _id;
        private static float _until,_damage;
        private static int _blastId;private static bool _lastMissile;
        private static string _text;
        private static TABGPlayerClient _recipient;
        private GUIStyle _style,_number;
        private sealed class Scope : IDisposable
        {
            private readonly bool active=_tagging,missile=_missile;private readonly int id=_id;
            public void Dispose(){_tagging=active;_missile=missile;_id=id;}
        }
        internal static IDisposable Context(bool missile,int id)
        {var scope=new Scope();_tagging=id>0;_missile=missile;_id=id;return scope;}
        [HarmonyPatch(typeof(PhotonServerHandler),"SendMessageToServer")]
        internal static class TagDamage
        {
            static void Prefix(EventCode __0,ref byte[] __1)
            {if(_tagging && ((byte)__0==214 || (byte)__0==17))__1=BlastReceipt.Tag(__1,_missile,_id);}
        }
        internal static void Receive(BinaryReader r)
        {
            bool missile=r.ReadBoolean(),vehicle=r.ReadBoolean();float amount=VehicleProtocol.ReadFinite(r);
            int blastId=r.BaseStream.Length-r.BaseStream.Position==4?r.ReadInt32():0;
            if(r.BaseStream.Position!=r.BaseStream.Length || amount<=0)return;
            var local=PhotonServerHandler.instance?.LocalPlayer;if(local==null)return;
            string label=(missile?"RAKETE":"FPV")+(vehicle?" · FAHRZEUG":" · TREFFER");
            _damage=Time.unscaledTime<_until && _blastId==blastId && _lastMissile==missile && _recipient==local?_damage+amount:amount;
            _blastId=blastId;_lastMissile=missile;CombatAudio.Hit();
            _text=label;_recipient=local;_until=Time.unscaledTime+4.2f;
        }
        private void OnGUI()
        {
            if(Time.unscaledTime>=_until || _recipient!=PhotonServerHandler.instance?.LocalPlayer)return;
            if(_style==null)
            {
                _style=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleCenter,fontSize=16,fontStyle=FontStyle.Bold};
                _number=new GUIStyle(_style){fontSize=36};_style.normal.textColor=Color.white;_number.normal.textColor=new Color(1,.92f,.28f);
            }
            var old=GUI.color;int depth=GUI.depth;GUI.depth=-110;
            float fade=Mathf.Clamp01((_until-Time.unscaledTime)/.7f),cx=Screen.width*.5f,top=Screen.height*.5f+45;
            GUI.color=new Color(0,0,0,.88f*fade);GUI.DrawTexture(new Rect(cx-150,top,300,95),Texture2D.whiteTexture);
            GUI.color=new Color(1,.85f,.12f,fade);GUI.DrawTexture(new Rect(cx-150,top,300,3),Texture2D.whiteTexture);
            GUI.color=new Color(1,1,1,fade);
            GUI.Label(new Rect(cx-25,Screen.height*.5f-25,50,50),"×",_number);
            GUI.Label(new Rect(cx-145,top+5,290,53),Mathf.CeilToInt(_damage)+" SCHADEN",_number);
            GUI.Label(new Rect(cx-145,top+60,290,28),_text,_style);
            GUI.color=old;GUI.depth=depth;
        }
    }
}
