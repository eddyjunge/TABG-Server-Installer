# Original procedural sounds; no third-party recordings or runtime synthesis cost.
from pathlib import Path
import math,random,wave,struct
root=Path(__file__).resolve().parents[1]/'TabgInstaller.FlyingControls/AudioAssets'
root.mkdir(parents=True,exist_ok=True)
rate=22050
for name,duration in [('heli',3),('missile',1),('lock',.32),('hit',.11),('warning',.15)]:
 rng=random.Random(153);data=[];low=0
 for i in range(int(rate*duration)):
  t=i/rate;noise=rng.uniform(-1,1);low=.84*low+.16*noise
  if name=='heli':
   pulse=(.5+.5*math.sin(2*math.pi*18*t))**9
   v=.42*pulse*(.65*math.sin(2*math.pi*72*t)+low*1.3)+.17*math.sin(2*math.pi*108*t)+.06*math.sin(2*math.pi*432*t)+.10*low
  elif name=='missile':v=.5*low+.1*math.sin(2*math.pi*620*t)
  elif name=='lock':
   phase=t if t<.14 else t-.17;v=(.32*math.sin(2*math.pi*(940 if t<.14 else 1350)*phase)) if t<.14 or t>.17 else 0
  elif name=='hit':v=(.6*low+.25*math.sin(2*math.pi*(1900-900*t/duration)*t))*math.exp(-t*35)
  else:v=.27*math.sin(2*math.pi*(1050-320*t/duration)*t)
  if name not in ('heli','missile'):v*=min(1,t/.006,(duration-t)/.02)
  data.append(v)
 if name in ('heli','missile'):
  # Blend the loop boundary into a shared short crossfade.
  n=500
  for i in range(n):
   a=i/n;data[-n+i]=(1-a)*data[-n+i]+a*data[i]
  data=data[n:]
  seam=data[0]-data[-1]
  for i in range(64):data[-64+i]+=seam*(i+1)/64
 with wave.open(str(root/(name+'.wav')),'wb') as w:
  w.setnchannels(1);w.setsampwidth(2);w.setframerate(rate)
  w.writeframes(b''.join(struct.pack('<h',int(max(-.95,min(.95,v))*32767)) for v in data))
 print(name,len(data)/rate,'s')
