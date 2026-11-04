namespace TabgInstaller.Vehicles
{
    internal enum BotRecoveryEvent { None, Start, Finished, Failed }
    internal sealed class BotRecoveryState
    {
        internal bool Active { get; private set; }
        private float _idle,_age;
        internal void Cancel(){Active=false;_idle=0;_age=0;}
        internal BotRecoveryEvent Tick(float dt,bool wantsMovement,bool horizontalProgress,bool planning,bool jumping,bool arrived)
        {
            if(Active)
            {
                _age+=dt;
                if(arrived){Cancel();return BotRecoveryEvent.Finished;}
                if(_age>=3){Cancel();return BotRecoveryEvent.Failed;}
                return BotRecoveryEvent.None;
            }
            if(!wantsMovement || horizontalProgress || planning || jumping){_idle=0;return BotRecoveryEvent.None;}
            _idle+=dt;if(_idle<1.5f)return BotRecoveryEvent.None;
            Active=true;_age=0;return BotRecoveryEvent.Start;
        }
    }
}
