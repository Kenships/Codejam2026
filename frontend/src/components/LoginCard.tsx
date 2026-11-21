import {Card, CardHeader, CardTitle, CardContent, CardDescription, CardFooter} from "@/components/ui/card"
import {Button} from "@/components/ui/button"

// --hue is set by LoginPage to the color picked in the hue slider
export const LoginCard = ( ) => {
    return (
        <Card className="w-full bg-black/70 ring-0 border-2 border-(--hue)">
            <div className="flex flex-col gap-2 p-2">
            <CardHeader>
                <CardTitle className="sm:text-2xl text-lg text-center text-(--hue)">Join a Game </CardTitle>
                <CardDescription className="sm:text-center sm:text-base text-sm text-center text-(--hue) ">Enter your username and the game code</CardDescription>
                {/* px-0: already inside CardHeader's padding; text-base: iOS zooms into inputs under 16px */}
                <CardContent className="flex flex-col gap-4 px-0">
                    <input type="text" placeholder="Username" className="border-2 border-(--hue) bg-black/50 rounded-md p-2 text-base text-(--hue) placeholder:text-(--hue)/60" />
                    <input type="text" placeholder="Game Code" className="border-2 border-(--hue) bg-black/50 rounded-md p-2 text-base text-(--hue) placeholder:text-(--hue)/60" />
                </CardContent>
                <CardFooter className="flex justify-center gap-4">
                    <Button className="transition-none bg-alpha text-(--hue) border-2 border-(--hue) px-4 py-2 rounded-md hover:bg-(--hue) hover:text-black">Join Game</Button>
                </CardFooter>
            </CardHeader>
             </div>
        </Card>
    )
}
