namespace CirrusFS.Flight
{
    /// Hooke's-law spring modelling control-surface actuator travel delay.
    /// Critically damped at damping = 1, so it eases to target without overshoot.
    public class ActuatorSpring
    {
        float position;
        float velocity;
        readonly float freq;
        readonly float damping;

        public ActuatorSpring(float freq = 12f, float damping = 1f)
        {
            this.freq = freq;
            this.damping = damping;
        }

        public float Position => position;

        public float Update(float dt, float target)
        {
            float k = freq * freq;
            float c = 2f * damping * freq;
            float a = k * (target - position) - c * velocity;
            velocity += a * dt;
            position += velocity * dt;
            return position;
        }

        public void Reset()
        {
            position = 0f;
            velocity = 0f;
        }
    }
}

