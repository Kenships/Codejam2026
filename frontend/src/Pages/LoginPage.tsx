import { useState, type CSSProperties } from 'react'
import PixelBlast from '../components/PixelBlast'
import { LoginCard } from '../components/LoginCard'
import { HueBar, colorAt } from '@/components/HueBar'

export function LoginPage() {
    // the whole UI takes the last picked color; white until something is picked
    const [pos, setPos] = useState<number | null>(null)
    const color = pos === null ? '#ffffff' : colorAt(pos)

    return (
        <div
            className="relative bg-black flex h-screen items-center justify-center"
            style={{ '--hue': color } as CSSProperties}
        >
            <div className="absolute inset-0">
                <PixelBlast variant="square"
                    pixelSize={10}
                    color="#000000"
                    accentColor={color}
                    patternDensity={3}
                    enableRipples={true}
                    glow={0.5}
                    drift={0.1}
                    hoverEffect="reveal"
                    rippleThickness={0.4}

                />
            </div>
            <div className="relative flex flex-col gap-2">
                <div className="flex flex-col gap-5">
                    <div className="flex flex-col ">
                        <h1 className="font-heading text-6xl font-semibold tracking-wider  uppercase text-center text-(--hue)">HueSlider</h1>
                        <p className=" font-heading text-center text-(--hue)">Pick a color for your game</p>
                    </div>
                    <HueBar value={pos} onChange={setPos} />

                    <LoginCard />
                </div>

            </div>
        </div>
    )
}
