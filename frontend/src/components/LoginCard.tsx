import {Card, CardHeader, CardTitle, CardContent, CardDescription, CardFooter} from "@/components/ui/card"
import {Button} from "@/components/ui/button"

// --hue is set by LoginPage to the color picked in the hue slider
export const LoginCard = ( ) => {
    return (
        <Card className="sm:w-[400px] w-[300px] bg-black/70 ring-0 border-2 border-(--hue)">
            <CardHeader>
                <CardTitle className="sm:text-2xl text-lg text-center text-(--hue)">Join a Game </CardTitle>
                <CardDescription className="sm:text-center sm:text-base text-sm text-center text-(--hue) ">Enter your username and the game code</CardDescription>
                <CardContent className="flex flex-col gap-4">
                    <input type="text" placeholder="Username" className="border-2 border-(--hue) bg-black/50 rounded-md p-2 text-(--hue) placeholder:text-(--hue)/60" />
                    <input type="text" placeholder="Game Code" className="border-2 border-(--hue) bg-black/50 rounded-md p-2 text-(--hue) placeholder:text-(--hue)/60" />
                </CardContent>
                <CardFooter className="flex justify-center">
                    <Button className="transition-none bg-alpha text-(--hue) border-2 border-(--hue) px-4 py-2 rounded-md hover:bg-(--hue) hover:text-black">Join Game</Button>
                </CardFooter>
            </CardHeader>
        </Card>
    )
}
