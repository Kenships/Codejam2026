import {Card, CardHeader, CardTitle, CardContent, CardDescription, CardFooter} from "@/components/ui/card"
import {Button} from "@/components/ui/button"

// --hue is set by LoginPage to the color picked in the hue slider
export const LoginCard = ( ) => {
    return (
        <Card className="w-full select-none bg-black/70 ring-0 border-2 border-(--hue) font-jersey">
            <div className="flex flex-col gap-2 p-2">
            <CardHeader>
                <CardTitle className="font-jersey sm:text-2xl text-lg select-none text-center text-(--hue) [text-shadow:0_0_8px_#000,0_0_16px_#000]">Join a Game </CardTitle>
                <CardDescription className="font-jersey select-none sm:text-center sm:text-base text-sm text-center text-(--hue) [text-shadow:0_0_6px_#000,0_0_12px_#000]">Enter your username and the game code</CardDescription>
                {/* px-0: already inside CardHeader's padding; text-base: iOS zooms into inputs under 16px */}
                <CardContent className="flex flex-col gap-4 px-0">
                    <input type="text" placeholder="Username" className="border-2 border-(--hue) font-jersey bg-black/50  p-2 text-base text-(--hue) placeholder:text-(--hue)/60" />
                    <input type="text" placeholder="Game Code" className="border-2 border-(--hue) font-jersey bg-black/50  p-2 text-base text-(--hue) placeholder:text-(--hue)/60" />
                </CardContent>
                <CardFooter className="flex justify-center gap-4">
                    <Button className="transition-none select-none bg-alpha text-(--hue) border-2 border-(--hue) font-jersey px-4 py-2  hover:bg-(--hue) hover:text-black">Join Game</Button>
                </CardFooter>
            </CardHeader>
             </div>
        </Card>
    )
}
