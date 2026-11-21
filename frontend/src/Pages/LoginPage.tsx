import PixelBlast from '../components/PixelBlast'
import {LoginCard} from '../components/LoginCard'

export function LoginPage() {
  return (
   <div className="relative bg-black flex h-screen items-center justify-center">
      <div className="absolute inset-0">
        <PixelBlast variant="square"
          pixelSize={10}
          color="#000000"
          accentColor="#FFFFFF"
          patternDensity={3}
          enableRipples={true}
          glow={0.5}
          drift={0.1}
          hoverEffect ="reveal"
          rippleThickness={0.4}

        />
      </div>
      {/* no z-index: it would isolate LoginCard's mix-blend text from PixelBlast */}
      <div className="relative">
        <LoginCard />
      </div>
    </div>
  )
}