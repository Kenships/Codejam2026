import { useState, type CSSProperties } from 'react'

import PixelBlast from '../components/PixelBlast'
import { LoginCard } from '../components/LoginCard'
import { TrueHueTitle } from '@/components/TrueHueTitle'
import { HueBar, colorAt } from '@/components/HueBar'

export function LoginPage() {
    // the whole UI takes the last picked color; white until something is picked
    const [pos, setPos] = useState<number | null>(null)
    const color = pos === null ? '#ffffff' : colorAt(pos)

    return (
        <div
            className="fixed inset-0 h-dvh overflow-hidden overscroll-none touch-none bg-black flex items-center justify-center px-4 py-4 sm:py-8"
            style={{ '--hue': color } as CSSProperties}
        >
            <div className="absolute inset-0">
                <PixelBlast variant="square"
                    pixelSize={10}
                    color="#000000"
                    accentColor={color}
                    patternDensity={4}
                    enableRipples={true}
                    glow={0.5}
                    drift={0.1}
                    hoverRadius={50}
                    hoverEffect="reveal"
                    rippleThickness={0.4}

                />
            </div>
            <div className="relative flex flex-col gap-2 w-full max-w-100">
                <div className="flex flex-col gap-3">
                    <div className="flex flex-col items-center select-none">
                        <TrueHueTitle />
                        <p className="font-jersey text-xl sm:text-2xl text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000,0_0_24px_#000]">Find the hue that matches the room</p>
                    </div>
                    <HueBar value={pos} onChange={setPos}  />

                    <LoginCard inkColor={color} />
                </div>

            </div>
        </div>
    )
}
