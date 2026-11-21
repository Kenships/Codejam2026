import {Card, CardHeader, CardTitle, CardContent, CardDescription, CardFooter} from "@/components/ui/card"
import {Button} from "@/components/ui/button"

export const LoginCard = () => {
    return (
        <Card className="sm:w-[400px] w-[300px] bg-black/50 border-2 border-white shadow-lg shadow-white">
            <CardHeader>
                <CardTitle className="sm:text-2xl text-lg text-center text-white">Join a Game </CardTitle>
                <CardDescription className="sm:text-center sm:text-base text-sm text-center text-white ">Enter your username and the game code</CardDescription>
                <CardContent className="flex flex-col gap-4">
                    <input type="text" placeholder="Username" className="border-2 border-white bg-black/50 rounded-md p-2 text-white placeholder:text-white" />
                    <input type="text" placeholder="Game Code" className="border-2 border-white bg-black/50 rounded-md p-2 text-white placeholder:text-white" />
                </CardContent>
                <CardFooter className="flex justify-center">
                    <Button className="bg-alpha text-white px-4 py-2 rounded-md hover:bg-black hover:text-white">Join Game</Button>
                </CardFooter>
            </CardHeader>
        </Card>
    )
}